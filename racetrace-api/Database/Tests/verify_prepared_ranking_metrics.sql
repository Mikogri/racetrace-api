begin;
do $$
declare original uuid; replacement uuid; target_session bigint; rider bigint;
begin
 select session_id,source_revision into target_session,original from public.ranking_session_preaggregates order by session_id limit 1;
 select rider_id into rider from public.results where session_id=target_session limit 1;
 assert target_session is not null;
 assert (public.get_prepared_rider_detail_data(p_rider_id=>rider,p_session_ids=>array[target_session])->>'complete')::boolean;
 assert not (public.get_prepared_rider_detail_data(p_algorithm_version=>'old-version',p_rider_id=>rider,p_session_ids=>array[target_session])->>'complete')::boolean;
 assert not (public.get_prepared_rider_detail_data(p_status=>'DNF',p_rider_id=>rider,p_session_ids=>array[target_session])->>'complete')::boolean;
 assert public.save_prepared_ranking_session(target_session,original,'test','[]');
 update public.ranking_session_preaggregates set payload=payload where session_id=target_session;
 select source_revision into replacement from public.ranking_session_preaggregates where session_id=target_session;
 assert replacement <> original;
 assert not public.save_prepared_ranking_session(target_session,original,'test','[]');
 assert (select prepared_payload is null and prepared_version is null from public.ranking_session_preaggregates where session_id=target_session);
 assert not (public.get_prepared_rider_detail_data(p_rider_id=>rider,p_session_ids=>array[target_session])->>'complete')::boolean;
 assert public.save_prepared_ranking_session(target_session,replacement,'test','[]');
 assert not has_function_privilege('anon','public.save_prepared_ranking_session(bigint,uuid,text,jsonb)','execute');
 assert not has_function_privilege('authenticated','public.save_prepared_ranking_session(bigint,uuid,text,jsonb)','execute');
 assert has_function_privilege('service_role','public.save_prepared_ranking_session(bigint,uuid,text,jsonb)','execute');
end;
$$;
rollback;
