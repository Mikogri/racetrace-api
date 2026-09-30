-- Measured single-rider payload grew 3.6%: retain the existing detail transport.
-- Remove only the two experimental functions introduced by separate_read_packages.
begin;
drop function public.get_rider_detail_compact_data(integer[], bigint, bigint[], text[], text[], text, integer[], text[], integer[], text[], text[], text[], date, date, text, bigint, bigint[]);
drop function public.compact_rider_detail_payload(jsonb);
commit;
notify pgrst, 'reload schema';
