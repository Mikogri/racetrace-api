begin;

alter table public.rounds
  add column if not exists status text not null default 'scheduled';

update public.rounds
set status = lower(btrim(status))
where status is distinct from lower(btrim(status));

alter table public.rounds
  drop constraint if exists rounds_status_check;

alter table public.rounds
  add constraint rounds_status_check
  check (status in ('scheduled', 'postponed', 'cancelled'));

create index if not exists rounds_status_season_idx
  on public.rounds (status, season_year, start_date);

comment on column public.rounds.status is
  'Administrative lifecycle status. Scheduled rounds still expose upcoming/live/completed according to dates; postponed and cancelled override that derived status.';

commit;
