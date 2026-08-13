begin;

-- Read-path indexes for public filters and joins. This script is idempotent
-- and does not modify imported domain data.
create index if not exists rounds_season_start_idx
  on public.rounds (season_year, start_date, id);

create index if not exists rounds_track_season_idx
  on public.rounds (track_id, season_year, start_date);

create index if not exists rounds_condition_season_idx
  on public.rounds (condition_id, season_year, start_date);

create index if not exists rounds_level_season_idx
  on public.rounds (competition_level_id, season_year, start_date);

create index if not exists sessions_round_category_type_idx
  on public.sessions (round_id, category_id, session_type, session_date, id);

create index if not exists sessions_category_type_date_idx
  on public.sessions (category_id, session_type, session_date, id);

create index if not exists results_session_position_idx
  on public.results (session_id, position, id);

create index if not exists results_rider_session_idx
  on public.results (rider_id, session_id, id);

create index if not exists results_team_rider_idx
  on public.results (team_id, rider_id)
  where team_id is not null;

create index if not exists results_session_status_idx
  on public.results (session_id, status, id);

create index if not exists time_splits_result_index_idx
  on public.time_splits (result_id, split_index);

create index if not exists result_points_result_points_idx
  on public.result_points (result_id, points_awarded);

create index if not exists riders_nation_name_idx
  on public.riders (nation, last_name, first_name, id);

create index if not exists tracks_country_venue_name_idx
  on public.tracks (country_code, venue, name, id);

commit;
