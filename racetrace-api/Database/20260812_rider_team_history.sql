begin;

-- Older database versions may expose this name as a regular or materialized
-- view. Preserve it temporarily so dependent functions remain valid until the
-- ranking RPC is redeployed, then create the writable history table below.
do $migration$
declare
  relation_kind "char";
begin
  select class.relkind
  into relation_kind
  from pg_catalog.pg_class class
  join pg_catalog.pg_namespace namespace
    on namespace.oid = class.relnamespace
  where namespace.nspname = 'public'
    and class.relname = 'rider_team_history';

  if relation_kind in ('v', 'm') then
    if to_regclass('public.rider_team_history_legacy') is not null then
      raise exception
        'public.rider_team_history_legacy already exists; inspect it before retrying the migration';
    end if;

    if relation_kind = 'm' then
      alter materialized view public.rider_team_history
        rename to rider_team_history_legacy;
    else
      alter view public.rider_team_history
        rename to rider_team_history_legacy;
    end if;
  elsif relation_kind is not null and relation_kind not in ('r', 'p') then
    raise exception
      'public.rider_team_history exists with unsupported relation kind %',
      relation_kind;
  end if;
end;
$migration$;

create table if not exists public.rider_team_history (
  id bigint generated always as identity primary key,
  rider_id bigint not null,
  team_id bigint not null,
  valid_from date not null,
  valid_to date,
  source_round_id bigint,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),

  constraint rider_team_history_rider_id_fkey
    foreign key (rider_id) references public.riders(id)
    on update cascade on delete cascade,
  constraint rider_team_history_team_id_fkey
    foreign key (team_id) references public.teams(id)
    on update cascade on delete restrict,
  constraint rider_team_history_source_round_id_fkey
    foreign key (source_round_id) references public.rounds(id)
    on update cascade on delete set null,
  constraint rider_team_history_valid_range_check
    check (valid_to is null or valid_to >= valid_from),
  constraint rider_team_history_rider_valid_from_key
    unique (rider_id, valid_from)
);

create index if not exists rider_team_history_lookup_idx
  on public.rider_team_history (rider_id, valid_from desc, valid_to)
  include (team_id, source_round_id);

create index if not exists rider_team_history_team_idx
  on public.rider_team_history (team_id, valid_from desc, rider_id);

create unique index if not exists rider_team_history_one_open_period_idx
  on public.rider_team_history (rider_id)
  where valid_to is null;

alter table public.rider_team_history enable row level security;

grant select on public.rider_team_history to anon, authenticated;
revoke insert, update, delete, truncate, references, trigger
  on public.rider_team_history from anon, authenticated;

drop policy if exists public_read_rider_team_history
  on public.rider_team_history;
create policy public_read_rider_team_history
  on public.rider_team_history
  for select
  to anon, authenticated
  using (true);

create or replace function public.refresh_rider_team_history(
  p_rider_ids bigint[] default '{}'
)
returns integer
language plpgsql
security invoker
set search_path = ''
as $function$
declare
  inserted_count integer := 0;
begin
  if coalesce(cardinality(p_rider_ids), 0) = 0 then
    return 0;
  end if;

  delete from public.rider_team_history history
  where history.rider_id = any(p_rider_ids);

  with dated_results as materialized (
    select distinct on (
      result.rider_id,
      coalesce(session_row.session_date, round_row.start_date::date)
    )
      result.rider_id,
      result.team_id,
      coalesce(session_row.session_date, round_row.start_date::date) as event_date,
      round_row.id as round_id,
      result.id as result_id
    from public.results result
    join public.sessions session_row on session_row.id = result.session_id
    join public.rounds round_row on round_row.id = session_row.round_id
    where result.rider_id = any(p_rider_ids)
      and result.team_id is not null
      and coalesce(session_row.session_date, round_row.start_date::date) is not null
    order by
      result.rider_id,
      coalesce(session_row.session_date, round_row.start_date::date),
      result.id desc
  ),
  change_markers as (
    select
      dated.*,
      case
        when lag(dated.team_id) over (
          partition by dated.rider_id
          order by dated.event_date, dated.result_id
        ) is distinct from dated.team_id
        then 1 else 0
      end as starts_new_period
    from dated_results dated
  ),
  segmented as (
    select
      marked.*,
      sum(marked.starts_new_period) over (
        partition by marked.rider_id
        order by marked.event_date, marked.result_id
      ) as period_number
    from change_markers marked
  ),
  periods as (
    select
      segmented.rider_id,
      segmented.team_id,
      min(segmented.event_date) as valid_from,
      (array_agg(
        segmented.round_id
        order by segmented.event_date, segmented.result_id
      ))[1] as source_round_id
    from segmented
    group by
      segmented.rider_id,
      segmented.team_id,
      segmented.period_number
  ),
  dated_periods as (
    select
      periods.*,
      lead(periods.valid_from) over (
        partition by periods.rider_id
        order by periods.valid_from
      ) - 1 as valid_to
    from periods
  )
  insert into public.rider_team_history (
    rider_id,
    team_id,
    valid_from,
    valid_to,
    source_round_id
  )
  select
    dated_periods.rider_id,
    dated_periods.team_id,
    dated_periods.valid_from,
    dated_periods.valid_to,
    dated_periods.source_round_id
  from dated_periods
  order by dated_periods.rider_id, dated_periods.valid_from;

  get diagnostics inserted_count = row_count;
  return inserted_count;
end;
$function$;

revoke all on function public.refresh_rider_team_history(bigint[]) from public;
grant execute on function public.refresh_rider_team_history(bigint[])
  to service_role;

-- Backfill all periods from existing results. Rows without a team are ignored
-- and therefore never create or close a period by themselves.
select public.refresh_rider_team_history(array(
  select distinct result.rider_id
  from public.results result
  where result.team_id is not null
  order by result.rider_id
));

notify pgrst, 'reload schema';

commit;
