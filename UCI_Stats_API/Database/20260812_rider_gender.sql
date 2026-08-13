begin;

alter table public.riders
  add column if not exists gender text;

alter table public.riders
  drop constraint if exists riders_gender_check;

alter table public.riders
  add constraint riders_gender_check
  check (gender is null or gender in ('male', 'female'));

with category_evidence as (
  select
    result.rider_id,
    bool_or(
      upper(category.code) in ('EM', 'JM', 'ME', 'MJ')
      or lower(btrim(category.name)) in (
        'elite men', 'junior men', 'men elite', 'men junior'
      )
    ) as has_male_category,
    bool_or(
      upper(category.code) in ('EW', 'JW', 'WE', 'WJ')
      or lower(btrim(category.name)) in (
        'elite women', 'junior women', 'women elite', 'women junior'
      )
    ) as has_female_category
  from public.results result
  join public.sessions session_row
    on session_row.id = result.session_id
  join public.categories category
    on category.id = session_row.category_id
  group by result.rider_id
),
resolved_gender as (
  select
    rider_id,
    case
      when has_male_category and not has_female_category then 'male'
      when has_female_category and not has_male_category then 'female'
      else null
    end as gender
  from category_evidence
)
update public.riders rider
set gender = resolved.gender
from resolved_gender resolved
where resolved.rider_id = rider.id
  and resolved.gender is not null
  and rider.gender is distinct from resolved.gender;

notify pgrst, 'reload schema';

commit;
