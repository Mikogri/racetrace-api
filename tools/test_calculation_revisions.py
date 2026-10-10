"""Exercise revision triggers on a disposable, local PostgreSQL schema (no Supabase credentials)."""
import json
import os
from pathlib import Path
import re
import subprocess


def main():
    if os.environ.get("PGHOST") not in ("127.0.0.1", "localhost", "::1"):
        raise SystemExit("Only explicitly configured loopback PostgreSQL is allowed.")
    if not re.fullmatch(r"racetrace_test_[A-Za-z0-9_]+", os.environ.get("PGDATABASE", "")):
        raise SystemExit("Use a disposable PGDATABASE named racetrace_test_<name>.")
    psql = os.environ.get("RACETRACE_TEST_PSQL", "psql")

    def sql(query):
        result = subprocess.run([psql, "-XqAt", "-v", "ON_ERROR_STOP=1"], input=query, text=True, capture_output=True)
        if result.returncode:
            raise RuntimeError(result.stderr)
        return result.stdout.strip()

    # Representative columns/FKs from the existing RaceTrace migrations; no production table is accessed.
    sql("""
        create table if not exists tracks(id bigint primary key,name text);
        create table if not exists conditions(id bigint primary key,name text);
        create table if not exists riders(id bigint primary key,name text);
        create table if not exists teams(id bigint primary key,name text);
        create table if not exists categories(id bigint primary key,name text);
        create table if not exists competition_levels(id bigint primary key,name text);
        create table if not exists points_systems(id bigint primary key,name text);
        create table if not exists competition_ranks(id bigint primary key,name text);
        create table if not exists point_system_stages(id bigint primary key,name text);
        create table if not exists point_rules(id bigint primary key,name text);
        create table if not exists rounds(id bigint primary key,season_year integer,track_id bigint references tracks,condition_id bigint references conditions);
        create table if not exists sessions(id bigint primary key,round_id bigint references rounds on delete cascade,condition_id bigint references conditions);
        create table if not exists round_categories(id bigint primary key,round_id bigint references rounds on delete cascade);
        create table if not exists results(id bigint primary key,session_id bigint references sessions on delete cascade,rider_id bigint references riders,
            team_id bigint references teams,position integer);
        create table if not exists time_splits(id bigint primary key,result_id bigint references results on delete cascade,time_ms integer);
        create table if not exists result_points(id bigint primary key,result_id bigint references results on delete cascade,points integer);
        create table if not exists ranking_session_preaggregates(session_id bigint primary key references sessions on delete cascade,payload jsonb);
        create table if not exists rider_team_history(id bigint primary key,rider_id bigint references riders,team_id bigint references teams);
        truncate tracks,conditions,riders,teams,categories,competition_levels,competition_ranks,points_systems,point_system_stages,point_rules,rounds,sessions,
            round_categories,results,time_splits,result_points,ranking_session_preaggregates,rider_team_history cascade;
    """)
    migration = Path(__file__).resolve().parents[1] / "supabase/migrations/20261010210121_calculation_revisions.sql"
    sql(migration.read_text(encoding="utf-8-sig"))
    sql("""
        insert into tracks values (1,'track one'),(2,'track two');
        insert into conditions values (1,'dry'),(2,'wet');
        insert into riders values (1,'rider one'),(2,'rider two');
        insert into teams values (1,'team one'),(2,'team two');
        insert into categories values (1,'elite');
        insert into points_systems values (1,'world cup');
        insert into competition_ranks values (1,'world cup');
        insert into rounds values (1,2025,1,1),(2,2026,2,1);
        insert into sessions values (11,1,1),(12,1,1),(21,2,1);
        insert into results values (111,11,1,1,1),(121,12,1,1,2),(211,21,2,2,1);
        insert into time_splits values (1,111,1000),(2,211,1100);
        insert into result_points values (1,111,100),(2,211,200);
        insert into rider_team_history values (1,1,1),(2,2,2);
    """)

    def revisions():
        return json.loads(sql("select public.get_calculation_revisions();"))

    def packages():
        sql("insert into ranking_session_preaggregates select id,'{}' from sessions on conflict do nothing;")

    before = revisions()
    assert revisions() == before, "Read-only revision lookup must not mutate its revision"
    assert json.loads(sql("set role service_role; select public.get_calculation_revisions(); reset role;")) == before, "Service role needs RLS read access"
    packages()
    sql("begin; update results set position=2 where id=111; update time_splits set time_ms=1020 where id=1; commit;")
    after = revisions()
    assert after['round:1'] == before['round:1'] + 1
    assert after['all'] == before['all'] + 1, "One committed import transaction must advance each scope only once"
    assert after['session:11'] == before['session:11'] + 1
    assert after['season:2025'] == before['season:2025'] + 1
    assert after['round:2'] == before['round:2'] and after['season:2026'] == before['season:2026']
    assert sql("select string_agg(session_id::text,',' order by session_id) from ranking_session_preaggregates;") == '21'
    packages()
    sql("begin; update result_points set points=999 where id=2; rollback;")
    assert revisions() == after, "Rolled-back edits must not invalidate committed cache identity"
    assert sql("select count(*) from ranking_session_preaggregates;") == '3', "Rollback must restore preaggregates too"
    sql("update results set session_id=21 where id=111;")
    moved = revisions()
    assert moved['round:1'] > after['round:1'] and moved['round:2'] > after['round:2'], "Both old and new parent scopes must change"
    packages()
    sql("update time_splits set result_id=211 where id=1;")
    assert revisions()['round:2'] > moved['round:2']
    packages()
    before = revisions()
    sql("update sessions set condition_id=2 where id=12;")
    after = revisions()
    assert after['round:1'] > before['round:1'] and after['round:2'] == before['round:2']
    packages()
    before = revisions()
    sql("update rounds set season_year=2024 where id=1;")
    after = revisions()
    assert after['season:2025'] > before['season:2025'] and after['season:2024'] >= 1
    packages()
    before = revisions()
    sql("update riders set name='renamed' where id=1;")
    after = revisions()
    assert after['round:1'] > before['round:1'] and after['round:2'] > before['round:2']
    packages()
    before = revisions()
    sql("update categories set name='elite men' where id=1;")
    assert revisions()['global'] > before['global']
    assert sql("select count(*) from ranking_session_preaggregates;") == '0'
    for dictionary in ('points_systems', 'competition_ranks'):
        packages()
        before = revisions()
        sql(f"update {dictionary} set name='revised rule' where id=1;")
        assert revisions()['global'] > before['global'], dictionary
        assert sql("select count(*) from ranking_session_preaggregates;") == '0', dictionary
    packages()
    before = revisions()
    sql("delete from rounds where id=1;")
    after = revisions()
    assert after['round:1'] > before['round:1'] and after['session:12'] > before['session:12']
    assert sql("select count(*) from results where id=121;") == '0'
    assert sql("select has_function_privilege('anon','public.get_calculation_revisions()','EXECUTE');") == 'f'
    assert sql("select has_function_privilege('authenticated','public.get_calculation_revisions()','EXECUTE');") == 'f'
    print("PASS: PostgreSQL scoped revisions; transaction deduplication; read-only lookup; service-role access; "
          "rollback; old/new relationships; session conditions; season moves; rider dependencies; global dictionaries; cascade deletes; preaggregate invalidation.")


if __name__ == '__main__':
    main()
