begin;

alter table public.rounds
  add column if not exists status text;

alter table public.rounds
  drop constraint if exists rounds_status_check;

update public.rounds
set status = case
  when status = 'postponed' then 'scheduled'
  when status is null or btrim(status) = '' then 'scheduled'
  else lower(btrim(status))
end;

update public.rounds
set status = 'scheduled'
where status not in ('scheduled', 'confirmed', 'live', 'cancelled');

alter table public.rounds
  alter column status set default 'scheduled',
  alter column status set not null;

alter table public.rounds
  add constraint rounds_status_check
  check (status in ('scheduled', 'confirmed', 'live', 'cancelled'));

comment on column public.rounds.status is
  'Administrative lifecycle status: scheduled, confirmed, live, or cancelled.';

commit;
