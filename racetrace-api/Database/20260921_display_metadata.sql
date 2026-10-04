begin;

-- Presentation metadata only. Calculator classification, session weights,
-- point-system stages and existing result records are deliberately unchanged.
create table if not exists public.session_types (
  code text primary key check (code = upper(btrim(code)) and code <> ''),
  name text not null check (btrim(name) <> ''),
  short_name text not null check (btrim(short_name) <> ''),
  abbreviation text not null check (btrim(abbreviation) <> ''),
  group_name text not null check (btrim(group_name) <> ''),
  display_order integer not null default 100,
  aliases text[] not null default '{}'
);

insert into public.session_types
  (code, name, short_name, abbreviation, group_name, display_order, aliases)
values
  ('SEEDING', 'Seeding', 'Seeding', 'S', 'Seeding', 10, array['S','Seeding Run','Seeding Round']),
  ('QUALI', 'Qualifying', 'Quali', 'Q', 'Qualifying', 20, array['Q','qualification','qualifying']),
  ('Q1', 'Qualifying 1', 'Quali 1', 'Q1', 'Qualifying 1', 30, array['qualifying_1','Quali 1','Qualification 1']),
  ('Q2', 'Qualifying 2', 'Quali 2', 'Q2', 'Qualifying 2', 40, array['qualifying_2','Quali 2','Qualification 2']),
  ('SEMI', 'Semi Final', 'Semi Final', 'SF', 'Semi Final', 50, array['SF','semi_final','semifinal','semi-final','semi-finals']),
  ('FINAL', 'Final', 'Final', 'F', 'Finals', 60, array['F','Finals','Final Run']),
  ('TIMED_TRAINING', 'Timed Training', 'Timed Training', 'O', 'Timed Training', 70, array['timedtraining']),
  ('PRACTICE', 'Training', 'Training', 'O', 'Training', 80, array['training']),
  ('OTHER', 'Other', 'Other', 'O', 'Other', 100, '{}')
on conflict (code) do nothing;

-- Keep every existing session code, including installations with custom types.
insert into public.session_types (code, name, short_name, abbreviation, group_name)
select distinct session_type, initcap(replace(session_type, '_', ' ')),
  initcap(replace(session_type, '_', ' ')), session_type,
  initcap(replace(session_type, '_', ' '))
from public.sessions
where session_type is not null
on conflict (code) do nothing;

-- A dictionary FK replaces the closed enumeration. New types can be registered
-- before import, without editing this constraint or any frontend label map.
do $$
begin
  if not exists (select 1 from pg_constraint
                 where conrelid = 'public.sessions'::regclass
                   and conname = 'sessions_session_type_fkey') then
    alter table public.sessions add constraint sessions_session_type_fkey
      foreign key (session_type) references public.session_types(code)
      on update restrict on delete restrict not valid;
  end if;
end $$;
alter table public.sessions validate constraint sessions_session_type_fkey;
alter table public.sessions drop constraint if exists sessions_session_type_check;

alter table public.categories
  add column if not exists short_name text,
  add column if not exists gender text,
  add column if not exists display_order integer;

-- Only initialize new presentation fields; never replace an administrator's labels.
update public.categories c set
  short_name = coalesce(c.short_name, seed.short_name),
  gender = coalesce(c.gender, seed.gender),
  display_order = coalesce(c.display_order, seed.display_order)
from (values
  ('EM','EM','male',10), ('EW','EW','female',20),
  ('JM','JM','male',30), ('JW','JW','female',40)
) seed(code, short_name, gender, display_order)
where c.code = seed.code
  and (c.short_name is null or c.gender is null or c.display_order is null);

update public.categories set display_order = 100 where display_order is null;
alter table public.categories alter column display_order set default 100;
alter table public.categories alter column display_order set not null;

do $$
begin
  if not exists (select 1 from pg_constraint where conrelid = 'public.categories'::regclass
                 and conname = 'categories_display_gender_check') then
    alter table public.categories add constraint categories_display_gender_check
      check (gender is null or gender in ('male','female','open'));
  end if;
  if not exists (select 1 from pg_constraint where conrelid = 'public.categories'::regclass
                 and conname = 'categories_short_name_not_blank') then
    alter table public.categories add constraint categories_short_name_not_blank
      check (short_name is null or btrim(short_name) <> '');
  end if;
end $$;

-- Status appearance/behavior stays in the client. Only the displayed wording
-- belongs here; the same raw status may have a different name in each context.
create table if not exists public.display_statuses (
  scope text not null check (scope in ('round','session')),
  code text not null check (code = lower(btrim(code)) and code <> ''),
  name text not null check (btrim(name) <> ''),
  short_name text not null check (btrim(short_name) <> ''),
  display_order integer not null default 100,
  aliases text[] not null default '{}',
  primary key (scope, code)
);
insert into public.display_statuses (scope, code, name, short_name, display_order, aliases)
select scope, code, name, name, display_order, aliases
from (values ('round'), ('session')) scopes(scope)
cross join (values
  ('live','Live',10,array['active','running']),
  ('completed','Completed',20,array['finished','final']),
  ('upcoming','Upcoming',30,array['scheduled','confirmed']),
  ('cancelled','Cancelled',40,array['canceled']),
  ('unknown','Unknown',100,'{}'::text[])
) statuses(code, name, display_order, aliases)
on conflict (scope, code) do nothing;

alter table public.session_types enable row level security;
alter table public.display_statuses enable row level security;
revoke all on public.session_types, public.display_statuses from anon, authenticated;
grant select on public.session_types, public.display_statuses to anon, authenticated;
grant select, insert, update, delete on public.session_types, public.display_statuses to service_role;
drop policy if exists session_types_read on public.session_types;
create policy session_types_read on public.session_types for select to anon, authenticated using (true);
drop policy if exists display_statuses_read on public.display_statuses;
create policy display_statuses_read on public.display_statuses for select to anon, authenticated using (true);

-- One small read, not one database query per record or per UI label. Invoker
-- security preserves the caller's existing category visibility and RLS.
create or replace function public.get_display_metadata()
returns jsonb language sql stable security invoker set search_path = '' as $$
  select jsonb_build_object(
    'categories', coalesce((select jsonb_agg(jsonb_build_object(
      'code', coalesce(c.code, c.name), 'name', c.name,
      'shortName', coalesce(c.short_name, c.code, c.name),
      'displayOrder', c.display_order, 'gender', coalesce(c.gender, 'open'),
      'minAge', c.min_age, 'maxAge', c.max_age
    ) order by c.display_order, c.id) from public.categories c), '[]'::jsonb),
    'sessionTypes', coalesce((select jsonb_agg(jsonb_build_object(
      'code', s.code, 'name', s.name, 'shortName', s.short_name,
      'abbreviation', s.abbreviation, 'groupName', s.group_name,
      'displayOrder', s.display_order, 'aliases', s.aliases
    ) order by s.display_order, s.code) from public.session_types s), '[]'::jsonb),
    'roundStatuses', coalesce((select jsonb_agg(jsonb_build_object(
      'code', s.code, 'name', s.name, 'shortName', s.short_name,
      'displayOrder', s.display_order, 'aliases', s.aliases
    ) order by s.display_order, s.code) from public.display_statuses s where s.scope = 'round'), '[]'::jsonb),
    'sessionStatuses', coalesce((select jsonb_agg(jsonb_build_object(
      'code', s.code, 'name', s.name, 'shortName', s.short_name,
      'displayOrder', s.display_order, 'aliases', s.aliases
    ) order by s.display_order, s.code) from public.display_statuses s where s.scope = 'session'), '[]'::jsonb)
  );
$$;
revoke all on function public.get_display_metadata() from public;
grant execute on function public.get_display_metadata() to anon, authenticated, service_role;
notify pgrst, 'reload schema';
commit;
