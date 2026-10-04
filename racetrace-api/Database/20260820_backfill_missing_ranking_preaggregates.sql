begin;

set local statement_timeout = '0';

do $backfill$
declare
  target_session record;
begin
  for target_session in
    select session_row.id
    from public.sessions session_row
    left join public.ranking_session_preaggregates aggregate_row
      on aggregate_row.session_id = session_row.id
    where aggregate_row.session_id is null
    order by session_row.id
  loop
    perform public.refresh_ranking_session_preaggregate(target_session.id);
  end loop;
end;
$backfill$;

commit;
