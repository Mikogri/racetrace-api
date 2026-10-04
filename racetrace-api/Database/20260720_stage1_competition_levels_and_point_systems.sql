 begin;

-- Stage 1 only: schema and reference data. Existing rounds and legacy point
-- systems are intentionally not backfilled by this script.

create table if not exists public.competition_levels (
  id bigint generated always as identity primary key,
  code text not null,
  name text not null,
  priority integer not null,
  description text,
  is_active boolean not null default true,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),

  constraint competition_levels_code_not_blank check (btrim(code) <> ''),
  constraint competition_levels_code_normalized check (code = lower(btrim(code))),
  constraint competition_levels_name_not_blank check (btrim(name) <> ''),
  constraint competition_levels_priority_positive check (priority > 0),
  constraint competition_levels_description_not_blank
    check (description is null or btrim(description) <> ''),
  constraint competition_levels_code_key unique (code),
  constraint competition_levels_priority_key unique (priority)
);

insert into public.competition_levels (code, name, priority, description)
values
  ('world_championship', 'World Championship', 1, 'UCI World Championships'),
  ('world_cup', 'World Cup', 2, 'UCI World Cup'),
  ('continental_championship', 'Continental Championship', 3, 'Continental championships'),
  ('national_championship', 'National Championship', 4, 'National championships'),
  ('other', 'Other', 5, 'Other competition level')
on conflict (code) do update
set name = excluded.name,
    priority = excluded.priority,
    description = excluded.description,
    is_active = true,
    updated_at = now();

alter table public.rounds
  add column if not exists competition_level_id bigint,
  add column if not exists is_season_final_round boolean not null default false;

do $$
begin
  if not exists (
    select 1
    from pg_constraint
    where conrelid = 'public.rounds'::regclass
      and conname = 'rounds_competition_level_id_fkey'
  ) then
    alter table public.rounds
      add constraint rounds_competition_level_id_fkey
      foreign key (competition_level_id)
      references public.competition_levels(id)
      on update cascade
      on delete restrict
      not valid;
  end if;

  -- NOT VALID preserves old rows without performing the omitted data migration,
  -- while PostgreSQL still enforces the rule for new and updated rows.
  if not exists (
    select 1
    from pg_constraint
    where conrelid = 'public.rounds'::regclass
      and conname = 'rounds_competition_level_required'
  ) then
    alter table public.rounds
      add constraint rounds_competition_level_required
      check (competition_level_id is not null)
      not valid;
  end if;
end $$;

create index if not exists rounds_competition_level_id_idx
  on public.rounds (competition_level_id);

alter table public.riders
  add column if not exists profile_image_url text,
  add column if not exists profile_image_updated_at timestamptz;

do $$
begin
  if not exists (
    select 1
    from pg_constraint
    where conrelid = 'public.riders'::regclass
      and conname = 'riders_profile_image_url_check'
  ) then
    alter table public.riders
      add constraint riders_profile_image_url_check
      check (
        profile_image_url is null or (
          profile_image_url = btrim(profile_image_url)
          and length(profile_image_url) between 1 and 2048
          and profile_image_url ~ '^https://'
        )
      );
  end if;
end $$;

-- Extend the existing point system table. Legacy columns are retained so that
-- no historical data is removed. They become optional for the new model.
alter table public.points_systems
  alter column series_id drop not null,
  alter column season_year drop not null,
  alter column competition_rank_id drop not null,
  alter column session_type drop not null,
  add column if not exists code text,
  add column if not exists competition_level_id bigint,
  add column if not exists discipline text default 'DHI',
  add column if not exists valid_from_season integer,
  add column if not exists valid_to_season integer,
  add column if not exists source_url text,
  add column if not exists notes text;

do $$
begin
  if not exists (
    select 1 from pg_constraint
    where conrelid = 'public.points_systems'::regclass
      and conname = 'points_systems_competition_level_id_fkey'
  ) then
    alter table public.points_systems
      add constraint points_systems_competition_level_id_fkey
      foreign key (competition_level_id)
      references public.competition_levels(id)
      on update cascade
      on delete restrict
      not valid;
  end if;

  if not exists (
    select 1 from pg_constraint
    where conrelid = 'public.points_systems'::regclass
      and conname = 'points_systems_new_model_required'
  ) then
    alter table public.points_systems
      add constraint points_systems_new_model_required
      check (
        code is not null
        and competition_level_id is not null
        and discipline is not null
        and valid_from_season is not null
        and valid_to_season is not null
      )
      not valid;
  end if;

  if not exists (
    select 1 from pg_constraint
    where conrelid = 'public.points_systems'::regclass
      and conname = 'points_systems_code_check'
  ) then
    alter table public.points_systems
      add constraint points_systems_code_check
      check (code is null or (code = lower(btrim(code)) and code <> ''))
      not valid;
  end if;

  if not exists (
    select 1 from pg_constraint
    where conrelid = 'public.points_systems'::regclass
      and conname = 'points_systems_discipline_check'
  ) then
    alter table public.points_systems
      add constraint points_systems_discipline_check
      check (discipline is null or btrim(discipline) <> '')
      not valid;
  end if;

  if not exists (
    select 1 from pg_constraint
    where conrelid = 'public.points_systems'::regclass
      and conname = 'points_systems_valid_seasons_check'
  ) then
    alter table public.points_systems
      add constraint points_systems_valid_seasons_check
      check (
        (valid_from_season is null and valid_to_season is null)
        or (
          valid_from_season between 1990 and 2100
          and valid_to_season between 1990 and 2100
          and valid_from_season <= valid_to_season
        )
      )
      not valid;
  end if;

  if not exists (
    select 1 from pg_constraint
    where conrelid = 'public.points_systems'::regclass
      and conname = 'points_systems_source_url_check'
  ) then
    alter table public.points_systems
      add constraint points_systems_source_url_check
      check (source_url is null or (length(source_url) <= 2048 and source_url ~ '^https?://'))
      not valid;
  end if;
end $$;

create unique index if not exists points_systems_code_unique_idx
  on public.points_systems (code);

create index if not exists points_systems_lookup_idx
  on public.points_systems (
    competition_level_id,
    category_id,
    discipline,
    valid_from_season,
    valid_to_season
  );

create table if not exists public.point_system_stages (
  id bigint generated always as identity primary key,
  point_system_id bigint not null,
  session_type text not null,
  awards_points boolean not null default true,
  awards_points_on_season_final_round boolean not null default true,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),

  constraint point_system_stages_point_system_id_fkey
    foreign key (point_system_id)
    references public.points_systems(id)
    on update cascade
    on delete cascade,
  constraint point_system_stages_session_type_check
    check (session_type = upper(btrim(session_type)) and session_type <> ''),
  constraint point_system_stages_system_session_key
    unique (point_system_id, session_type)
);

create table if not exists public.point_rules (
  id bigint generated always as identity primary key,
  point_system_stage_id bigint not null,
  position_from integer not null,
  position_to integer,
  points integer not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),

  constraint point_rules_stage_id_fkey
    foreign key (point_system_stage_id)
    references public.point_system_stages(id)
    on update cascade
    on delete cascade,
  constraint point_rules_position_from_check check (position_from >= 1),
  constraint point_rules_position_to_check
    check (position_to is null or position_to >= position_from),
  constraint point_rules_points_check check (points >= 0)
);

create index if not exists point_rules_stage_range_idx
  on public.point_rules (point_system_stage_id, position_from, position_to);

alter table public.result_points
  alter column points_awarded set default 0,
  add column if not exists point_system_stage_id bigint,
  add column if not exists point_rule_id bigint;

do $$
begin
  if not exists (
    select 1 from pg_constraint
    where conrelid = 'public.result_points'::regclass
      and conname = 'result_points_stage_id_fkey'
  ) then
    alter table public.result_points
      add constraint result_points_stage_id_fkey
      foreign key (point_system_stage_id)
      references public.point_system_stages(id)
      on update cascade
      on delete set null;
  end if;

  if not exists (
    select 1 from pg_constraint
    where conrelid = 'public.result_points'::regclass
      and conname = 'result_points_rule_id_fkey'
  ) then
    alter table public.result_points
      add constraint result_points_rule_id_fkey
      foreign key (point_rule_id)
      references public.point_rules(id)
      on update cascade
      on delete set null;
  end if;
end $$;

create unique index if not exists result_points_result_system_unique_idx
  on public.result_points (result_id, points_system_id);

create index if not exists result_points_rule_id_idx
  on public.result_points (point_rule_id);

-- Keep updated_at behavior consistent with the existing schema when the shared
-- trigger function is available.
do $$
begin
  if to_regprocedure('public.set_updated_at()') is not null then
    drop trigger if exists set_competition_levels_updated_at on public.competition_levels;
    create trigger set_competition_levels_updated_at
      before update on public.competition_levels
      for each row execute function public.set_updated_at();

    drop trigger if exists set_point_system_stages_updated_at on public.point_system_stages;
    create trigger set_point_system_stages_updated_at
      before update on public.point_system_stages
      for each row execute function public.set_updated_at();

    drop trigger if exists set_point_rules_updated_at on public.point_rules;
    create trigger set_point_rules_updated_at
      before update on public.point_rules
      for each row execute function public.set_updated_at();
  end if;
end $$;

alter table public.competition_levels enable row level security;
alter table public.point_system_stages enable row level security;
alter table public.point_rules enable row level security;

grant select on public.competition_levels, public.point_system_stages, public.point_rules
  to anon, authenticated;
revoke insert, update, delete, truncate, references, trigger
  on public.competition_levels, public.point_system_stages, public.point_rules
  from anon, authenticated;

grant all on public.competition_levels, public.point_system_stages, public.point_rules
  to service_role;
grant usage, select on all sequences in schema public to service_role;

drop policy if exists public_read_competition_levels on public.competition_levels;
create policy public_read_competition_levels
  on public.competition_levels for select
  to anon, authenticated
  using (true);

drop policy if exists public_read_point_system_stages on public.point_system_stages;
create policy public_read_point_system_stages
  on public.point_system_stages for select
  to anon, authenticated
  using (true);

drop policy if exists public_read_point_rules on public.point_rules;
create policy public_read_point_rules
  on public.point_rules for select
  to anon, authenticated
  using (true);

comment on column public.rounds.competition_level_id is
  'Required for new rounds. Existing rows are intentionally not backfilled in stage 1.';
comment on column public.riders.profile_image_url is
  'Public source URL only. Image bytes are not stored by this project.';
comment on table public.point_system_stages is
  'Session stages belonging to a historical point system.';
comment on table public.point_rules is
  'Non-overlapping position ranges and awarded points for one point-system stage.';

commit;

-- After a separate data migration assigns every old round a competition level:
-- alter table public.rounds validate constraint rounds_competition_level_required;
-- alter table public.rounds alter column competition_level_id set not null;
