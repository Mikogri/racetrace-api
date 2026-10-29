-- Match the former context-filter ordering and limits; preserve table RLS.
create or replace function public.get_filter_options()
returns jsonb
language sql
stable
security invoker
set search_path = ''
as $$
    select jsonb_build_object(
        'seasons', coalesce((
            select jsonb_agg(s.season_year order by s.season_year desc)
            from (select distinct r.season_year from (
                select season_year from public.rounds order by season_year desc limit 1000
            ) r where r.season_year is not null) s
        ), '[]'::jsonb),
        'tracks', coalesce((
            select jsonb_agg(jsonb_build_object('id', t.id, 'name', t.name,
                'venue', t.venue, 'countryCode', t.country_code) order by t.name, t.id)
            from (select id, name, venue, country_code from public.tracks order by name, id limit 150) t
        ), '[]'::jsonb),
        'conditions', coalesce((
            select jsonb_agg(jsonb_build_object('id', c.id, 'name', c.name) order by c.name, c.id)
            from (select id, name from public.conditions order by name, id limit 100) c
        ), '[]'::jsonb),
        'competitionClasses', coalesce((
            select jsonb_agg(jsonb_build_object('id', c.id, 'name', c.name) order by c.priority, c.id)
            from public.competition_levels c
        ), '[]'::jsonb)
    );
$$;

revoke all on function public.get_filter_options() from public;
grant execute on function public.get_filter_options() to anon, authenticated, service_role;
notify pgrst, 'reload schema';
