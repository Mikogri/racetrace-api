-- Opt-in shared Fantasy storage. Apply separately; never invoked by API startup.
-- SECURITY INVOKER, service-role-only RPCs. No public client can acquire or publish a job.
begin;
create schema if not exists racetrace_jobs;
revoke all on schema racetrace_jobs from public, anon, authenticated;
grant usage on schema racetrace_jobs to service_role;

create table if not exists racetrace_jobs.leases (
    job_key text primary key,
    run_id text not null,
    owner_id text not null,
    lease_token bigint not null default 1,
    lease_expires_at timestamptz not null,
    last_heartbeat_at timestamptz not null,
    last_success_at timestamptz,
    status text not null check (status in ('running','succeeded','failed'))
);

-- Successful runs ARE the published artifacts: one row per business cycle, no partial intermediate data.
create table if not exists racetrace_jobs.runs (
    job_key text not null references racetrace_jobs.leases(job_key),
    run_id text not null,
    owner_id text not null,
    lease_token bigint not null,
    attempt integer not null default 1,
    status text not null check (status in ('running','succeeded','failed')),
    started_at timestamptz not null,
    completed_at timestamptz,
    artifact_kind text,
    artifact_key text,
    computed_at timestamptz,
    payload jsonb,
    primary key (job_key, run_id),
    check (status <> 'succeeded' or (payload is not null and computed_at is not null and artifact_kind is not null and artifact_key is not null))
);
create index if not exists fantasy_artifact_latest on racetrace_jobs.runs(artifact_kind, artifact_key, computed_at desc, completed_at desc)
    where status = 'succeeded';
alter table racetrace_jobs.leases enable row level security;
alter table racetrace_jobs.runs enable row level security;
revoke all on racetrace_jobs.leases, racetrace_jobs.runs from public, anon, authenticated;
grant select, insert, update on racetrace_jobs.leases, racetrace_jobs.runs to service_role;
drop policy if exists service_jobs on racetrace_jobs.leases;
create policy service_jobs on racetrace_jobs.leases for all to service_role using (true) with check (true);
drop policy if exists service_runs on racetrace_jobs.runs;
create policy service_runs on racetrace_jobs.runs for all to service_role using (true) with check (true);

create or replace function public.fantasy_job_acquire(p_job_key text, p_run_id text, p_owner_id text, p_lease_seconds integer)
returns jsonb language plpgsql security invoker set search_path = '' as $$
declare
    job racetrace_jobs.leases%rowtype;
    prior racetrace_jobs.runs%rowtype;
    stamp timestamptz;
begin
    if p_job_key is null or length(p_job_key) not between 1 and 200
        or p_run_id is null or length(p_run_id) not between 1 and 200
        or p_owner_id is null or length(p_owner_id) not between 1 and 200
        or p_lease_seconds is null or p_lease_seconds not between 10 and 600 then
        raise exception 'Invalid Fantasy lease arguments' using errcode = '22023';
    end if;
    insert into racetrace_jobs.leases(job_key,run_id,owner_id,lease_expires_at,last_heartbeat_at,status)
    values(p_job_key,p_run_id,p_owner_id,clock_timestamp(),clock_timestamp(),'failed')
    on conflict (job_key) do nothing;
    -- Row lock is held only for this short RPC, never while external data is fetched.
    select * into job from racetrace_jobs.leases where job_key = p_job_key for update;
    stamp := clock_timestamp();
    select * into prior from racetrace_jobs.runs where job_key = p_job_key and run_id = p_run_id;
    if prior.status = 'succeeded' then
        return jsonb_build_object('completed',true,'payload',prior.payload);
    end if;
    if job.status = 'running' and job.lease_expires_at > stamp then
        if job.owner_id = p_owner_id and job.run_id = p_run_id then
            return jsonb_build_object('acquired',true,'leaseToken',job.lease_token,'attempt',prior.attempt);
        end if;
        return jsonb_build_object('acquired',false);
    end if;
    update racetrace_jobs.runs set status = 'failed', completed_at = stamp
    where job_key = p_job_key and run_id = job.run_id and lease_token = job.lease_token and status = 'running';
    update racetrace_jobs.leases set run_id = p_run_id, owner_id = p_owner_id,
        lease_token = lease_token + 1, lease_expires_at = stamp + make_interval(secs => p_lease_seconds),
        last_heartbeat_at = stamp, status = 'running'
    where job_key = p_job_key returning * into job;
    insert into racetrace_jobs.runs(job_key,run_id,owner_id,lease_token,status,started_at)
    values(p_job_key,p_run_id,p_owner_id,job.lease_token,'running',stamp)
    on conflict (job_key,run_id) do update set owner_id = excluded.owner_id, lease_token = excluded.lease_token,
        status = 'running', started_at = stamp, completed_at = null, attempt = racetrace_jobs.runs.attempt + 1
    returning * into prior;
    return jsonb_build_object('acquired',true,'leaseToken',job.lease_token,'attempt',prior.attempt);
end;
$$;

create or replace function public.fantasy_job_renew(p_job_key text,p_run_id text,p_owner_id text,p_lease_token bigint,p_lease_seconds integer)
returns boolean language plpgsql security invoker set search_path = '' as $$
begin
    if p_lease_seconds is null or p_lease_seconds not between 10 and 600 then
        raise exception 'Invalid lease duration' using errcode = '22023';
    end if;
    update racetrace_jobs.leases set lease_expires_at = clock_timestamp() + make_interval(secs => p_lease_seconds),
        last_heartbeat_at = clock_timestamp()
    where job_key = p_job_key and run_id = p_run_id and owner_id = p_owner_id and lease_token = p_lease_token
        and status = 'running' and lease_expires_at > clock_timestamp();
    return found;
end;
$$;

create or replace function public.fantasy_job_publish(p_job_key text,p_run_id text,p_owner_id text,p_lease_token bigint,
    p_kind text,p_artifact_key text,p_computed_at timestamptz,p_payload jsonb)
returns boolean language plpgsql security invoker set search_path = '' as $$
declare
    job racetrace_jobs.leases%rowtype;
    prior racetrace_jobs.runs%rowtype;
    stamp timestamptz;
begin
    if p_kind not in ('prices','forecast') or p_kind is null or p_artifact_key is null
        or p_job_key <> p_kind || ':' || p_artifact_key or p_payload is null or p_computed_at is null then
        raise exception 'Invalid Fantasy publication' using errcode = '22023';
    end if;
    select * into job from racetrace_jobs.leases where job_key = p_job_key for update;
    stamp := clock_timestamp();
    select * into prior from racetrace_jobs.runs where job_key = p_job_key and run_id = p_run_id;
    -- A response lost after commit can be retried, but never changed into a different result.
    if prior.status = 'succeeded' then
        return prior.owner_id = p_owner_id and prior.lease_token = p_lease_token and prior.payload = p_payload
            and prior.artifact_kind = p_kind and prior.artifact_key = p_artifact_key and prior.computed_at = p_computed_at;
    end if;
    if job.job_key is null or job.run_id <> p_run_id or job.owner_id <> p_owner_id or job.lease_token <> p_lease_token
        or job.status <> 'running' or job.lease_expires_at <= stamp then return false; end if;
    update racetrace_jobs.runs set status = 'succeeded', completed_at = stamp, artifact_kind = p_kind,
        artifact_key = p_artifact_key, computed_at = p_computed_at, payload = p_payload
    where job_key = p_job_key and run_id = p_run_id and owner_id = p_owner_id and lease_token = p_lease_token;
    if not found then raise exception 'Fantasy run identity missing'; end if;
    update racetrace_jobs.leases set status = 'succeeded', last_success_at = stamp, lease_expires_at = stamp
    where job_key = p_job_key;
    return true;
end;
$$;

create or replace function public.fantasy_job_fail(p_job_key text,p_run_id text,p_owner_id text,p_lease_token bigint)
returns boolean language plpgsql security invoker set search_path = '' as $$
begin
    update racetrace_jobs.leases set status = 'failed', lease_expires_at = clock_timestamp()
    where job_key = p_job_key and run_id = p_run_id and owner_id = p_owner_id and lease_token = p_lease_token
        and status = 'running' and lease_expires_at > clock_timestamp();
    if not found then return false; end if;
    update racetrace_jobs.runs set status = 'failed', completed_at = clock_timestamp()
    where job_key = p_job_key and run_id = p_run_id and owner_id = p_owner_id and lease_token = p_lease_token and status = 'running';
    return true;
end;
$$;

create or replace function public.fantasy_artifact_read(p_kind text,p_artifact_key text,p_cutoff timestamptz default null)
returns jsonb language sql stable security invoker set search_path = '' as $$
    select payload from racetrace_jobs.runs
    where status = 'succeeded' and artifact_kind = p_kind and artifact_key = p_artifact_key
        and (p_cutoff is null or computed_at <= p_cutoff)
    order by computed_at desc, completed_at desc limit 1;
$$;

create or replace function public.fantasy_job_status(p_job_key text)
returns jsonb language sql stable security invoker set search_path = '' as $$
    select jsonb_build_object('status',l.status,'lastAttemptAt',r.started_at,'lastSuccessAt',l.last_success_at)
    from racetrace_jobs.leases l join racetrace_jobs.runs r on r.job_key=l.job_key and r.run_id=l.run_id
    where l.job_key=p_job_key;
$$;

revoke all on function public.fantasy_job_acquire(text,text,text,integer),
    public.fantasy_job_renew(text,text,text,bigint,integer),
    public.fantasy_job_publish(text,text,text,bigint,text,text,timestamptz,jsonb),
    public.fantasy_job_fail(text,text,text,bigint), public.fantasy_artifact_read(text,text,timestamptz), public.fantasy_job_status(text)
from public, anon, authenticated;
grant execute on function public.fantasy_job_acquire(text,text,text,integer),
    public.fantasy_job_renew(text,text,text,bigint,integer),
    public.fantasy_job_publish(text,text,text,bigint,text,text,timestamptz,jsonb),
    public.fantasy_job_fail(text,text,text,bigint), public.fantasy_artifact_read(text,text,timestamptz), public.fantasy_job_status(text)
to service_role;
commit;
