-- Metadata corrections must be visible even when historical timing packages are reused.
create or replace function public.get_ranking_input_packages(p_session_ids bigint[],p_include_splits boolean default true)
returns table(session_id bigint,payload jsonb)
language sql stable security invoker set search_path='' as $$
select c.session_id,jsonb_build_object(
  'v',3,'s',c.payload->'s','r',c.payload->'r',
  'd',coalesce((select jsonb_agg(jsonb_build_array(
    ri.id,ri.uci_id,ri.first_name,ri.last_name,ri.nation,ri.profile_image_url,
    d->6,d->7,ri.uci_code,ri.birth_date,ri.has_injury,d->11,ri.gender))
    from jsonb_array_elements(c.payload->'d') d join public.riders ri on ri.id=(d->>0)::bigint),'[]'),
  't',coalesce((select jsonb_agg(jsonb_build_array(te.id,te.name,te.code))
    from jsonb_array_elements(c.payload->'t') t join public.teams te on te.id=(t->>0)::bigint),'[]'),
  'ix',c.input_splits,'x',case when p_include_splits then c.payload->'x' else '[]'::jsonb end)
from public.ranking_session_preaggregates c
where c.session_id=any(coalesce(p_session_ids,'{}'::bigint[])) and c.input_splits is not null
order by c.session_id;
$$;
revoke all on function public.get_ranking_input_packages(bigint[],boolean) from public;
grant execute on function public.get_ranking_input_packages(bigint[],boolean) to anon,authenticated,service_role;

create or replace function public.get_ranking_points_summary(p_session_ids bigint[])
returns jsonb language sql stable security invoker set search_path='' as $$
with requested as (select distinct unnest(coalesce(p_session_ids,'{}'::bigint[])) id),
packages as materialized (
 select c.session_id,c.payload from public.ranking_session_preaggregates c join requested r on r.id=c.session_id
),
totals as (
 select (r->>1)::bigint rider_id,sum(coalesce((r->>8)::numeric,0)) points
 from packages p cross join lateral jsonb_array_elements(p.payload->'r') r group by r->>1
)
select jsonb_build_object(
 'complete',not exists(select 1 from requested r where not exists(select 1 from packages p where p.session_id=r.id)),
 'rows',coalesce((select jsonb_agg(jsonb_build_object(
  'rider_id',t.rider_id::text,'points',t.points,
  'rider',jsonb_build_object('id',ri.id,'uci_id',ri.uci_id,'first_name',ri.first_name,'last_name',ri.last_name)
 ) order by t.rider_id::text) from totals t join public.riders ri on ri.id=t.rider_id),'[]'::jsonb));
$$;
revoke all on function public.get_ranking_points_summary(bigint[]) from public;
grant execute on function public.get_ranking_points_summary(bigint[]) to anon,authenticated,service_role;
