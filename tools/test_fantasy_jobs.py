"""Real PostgreSQL RPC concurrency tests. Only explicitly named loopback test databases are accepted."""
import concurrent.futures
import json
import os
from pathlib import Path
import re
import subprocess
import threading


def main():
    if os.environ.get("PGHOST") not in ("localhost", "127.0.0.1", "::1"):
        raise SystemExit("Set PGHOST to a loopback address; remote databases are forbidden.")
    if not re.fullmatch(r"racetrace_test_[A-Za-z0-9_]+", os.environ.get("PGDATABASE", "")):
        raise SystemExit("PGDATABASE must start with racetrace_test_. Never use production.")
    psql = os.environ.get("RACETRACE_TEST_PSQL", "psql")

    def sql(query):
        result = subprocess.run([psql, "-XAt", "-v", "ON_ERROR_STOP=1"], input=query, text=True, capture_output=True)
        if result.returncode:
            raise RuntimeError(result.stderr)
        return result.stdout.strip()

    def rpc(query):
        return json.loads(sql("select to_jsonb(" + query + ");"))

    migration = Path(__file__).resolve().parents[1] / "supabase/migrations/20261010210124_fantasy_job_coordination.sql"
    sql(migration.read_text(encoding="utf-8-sig"))
    # Only test-owned rows are removed. Reapplying the migration retains other runs/artifacts.
    sql("delete from racetrace_jobs.runs where job_key like 'prices:test-%'; "
        "delete from racetrace_jobs.leases where job_key like 'prices:test-%';")
    barrier = threading.Barrier(8)

    def acquire(index):
        barrier.wait()
        return index, rpc(f"public.fantasy_job_acquire('prices:test-race','scheduled:1','worker-{index}',10)")

    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as workers:
        acquired = list(workers.map(acquire, range(8)))
    winners = [(i, row) for i, row in acquired if row.get("acquired")]
    assert len(winners) == 1, acquired
    owner, first = winners[0]
    old_token = first["leaseToken"]
    assert rpc(f"public.fantasy_job_renew('prices:test-race','scheduled:1','worker-{owner}',{old_token},10)")
    assert not rpc("public.fantasy_job_acquire('prices:test-race','manual:1','manual-worker',10)")["acquired"]
    sql("update racetrace_jobs.leases set lease_expires_at = clock_timestamp() - interval '1 second' "
        "where job_key = 'prices:test-race';")
    current = rpc("public.fantasy_job_acquire('prices:test-race','scheduled:1','replacement',10)")
    assert current["acquired"] and current["leaseToken"] > old_token
    token = current["leaseToken"]
    args = "'prices','test-race','2026-10-10T12:00:00Z','{\"id\":\"good\"}'::jsonb"
    assert not rpc(f"public.fantasy_job_publish('prices:test-race','scheduled:1','worker-{owner}',{old_token},{args})")
    assert not rpc(f"public.fantasy_job_renew('prices:test-race','scheduled:1','worker-{owner}',{old_token},10)")
    assert not rpc(f"public.fantasy_job_fail('prices:test-race','scheduled:1','worker-{owner}',{old_token})")
    assert rpc(f"public.fantasy_job_publish('prices:test-race','scheduled:1','replacement',{token},{args})")
    assert rpc(f"public.fantasy_job_publish('prices:test-race','scheduled:1','replacement',{token},{args})")
    assert sql("select count(*) from racetrace_jobs.runs where job_key='prices:test-race' and status='succeeded';") == "1"
    replay = rpc("public.fantasy_job_acquire('prices:test-race','scheduled:1','third-worker',10)")
    assert replay["completed"] and replay["payload"] == {"id": "good"}
    assert rpc("public.fantasy_artifact_read('prices','test-race',null)") == {"id": "good"}
    retry = rpc("public.fantasy_job_acquire('prices:test-race','scheduled:2','retry-worker',10)")
    assert retry["acquired"]
    assert rpc(f"public.fantasy_job_fail('prices:test-race','scheduled:2','retry-worker',{retry['leaseToken']})")
    assert rpc("public.fantasy_artifact_read('prices','test-race',null)") == {"id": "good"}
    status = rpc("public.fantasy_job_status('prices:test-race')")
    assert status['status'] == 'failed' and status['lastSuccessAt'] is not None and status['lastAttemptAt'] is not None
    second = rpc("public.fantasy_job_acquire('prices:test-race','scheduled:2','retry-worker',10)")
    assert second["acquired"] and second["attempt"] == 2
    assert rpc(f"public.fantasy_job_publish('prices:test-race','scheduled:2','retry-worker',{second['leaseToken']},"
               "'prices','test-race','2026-10-11T12:00:00Z','{\"id\":\"new\"}'::jsonb)")
    assert rpc("public.fantasy_artifact_read('prices','test-race','2026-10-10T13:00:00Z')") == {"id": "good"}
    assert rpc("public.fantasy_artifact_read('prices','test-race',null)") == {"id": "new"}
    assert rpc("public.fantasy_job_acquire('prices:test-race','scheduled:1','late-worker',10)")["completed"]
    abandoned = rpc("public.fantasy_job_acquire('prices:test-race','scheduled:3','abandoned-worker',10)")
    assert abandoned['acquired']
    sql("update racetrace_jobs.leases set lease_expires_at = clock_timestamp() - interval '1 second' where job_key='prices:test-race';")
    assert rpc("public.fantasy_job_acquire('prices:test-race','manual:recovery','recovery-worker',10)")['acquired']
    assert sql("select status from racetrace_jobs.runs where job_key='prices:test-race' and run_id='scheduled:3';") == 'failed'
    assert sql("select has_function_privilege('anon','public.fantasy_job_acquire(text,text,text,integer)','EXECUTE');") == "f"
    assert sql("select has_function_privilege('authenticated','public.fantasy_artifact_read(text,text,timestamptz)','EXECUTE');") == "f"
    # Exercise SECURITY INVOKER schema/table permissions with the actual service role.
    assert json.loads(sql("set role service_role; select public.fantasy_artifact_read('prices','test-race',null); reset role;").splitlines()[-2]) == {"id": "new"}
    print("PASS: 8 competing PostgreSQL sessions; heartbeat; expiry/takeover; stale-owner fencing; atomic publication; "
          "idempotent retry; completed-cycle suppression; preserved last good; cutoff history; restricted roles.")


if __name__ == "__main__":
    main()
