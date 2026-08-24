begin;

alter table public.rounds
  drop constraint if exists rounds_status_check;

alter table public.rounds
  add constraint rounds_status_check
  check (status in ('scheduled', 'live', 'postponed', 'cancelled'));

comment on column public.rounds.status is
  'Administrative lifecycle status. Live explicitly exposes an active round; postponed and cancelled override date-derived status.';

commit;
