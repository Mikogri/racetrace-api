begin;

alter table public.sessions
    add column if not exists condition_id bigint;

-- Avoid referencing session_id on session rows. PostgreSQL resolves record fields
-- before CASE short-circuiting, so the previous single CASE failed on sessions.
create or replace function public.invalidate_ranking_session_preaggregate()
returns trigger
language plpgsql
security definer
set search_path = ''
as $$
declare
    affected_session_id bigint;
begin
    if tg_table_name = 'sessions' then
        if tg_op = 'DELETE' then
            affected_session_id := old.id;
        else
            affected_session_id := new.id;
        end if;
    elsif tg_op = 'DELETE' then
        affected_session_id := old.session_id;
    else
        affected_session_id := new.session_id;
    end if;

    delete from public.ranking_session_preaggregates
    where session_id = affected_session_id;

    if tg_op = 'DELETE' then
        return old;
    end if;

    return new;
end;
$$;

update public.sessions as session
set condition_id = round.condition_id
from public.rounds as round
where round.id = session.round_id
  and session.condition_id is null;

do $$
begin
    if not exists (
        select 1
        from pg_constraint
        where conrelid = 'public.sessions'::regclass
          and conname = 'sessions_condition_id_fkey'
    ) then
        alter table public.sessions
            add constraint sessions_condition_id_fkey
            foreign key (condition_id)
            references public.conditions(id)
            on update cascade
            on delete restrict;
    end if;
end
$$;

create index if not exists sessions_condition_id_idx
    on public.sessions(condition_id);

-- Correct the per-session conditions from the latest imported Les Gets round.
update public.sessions as session
set condition_id = condition.id
from public.rounds as round
cross join public.conditions as condition
where session.round_id = round.id
  and round.context_name = '20260821_mtb'
  and upper(session.session_type) in ('Q1', 'Q2')
  and lower(condition.name) = 'muddy';

update public.sessions as session
set condition_id = condition.id
from public.rounds as round
cross join public.conditions as condition
where session.round_id = round.id
  and round.context_name = '20260821_mtb'
  and upper(session.session_type) in ('FINAL', 'FINALS')
  and lower(condition.name) = 'mixed';

commit;
