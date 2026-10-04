begin;

-- Category ids stay unchanged, preserving every existing foreign-key relationship.
update public.categories
set
  code = case upper(btrim(code))
    when 'ME' then 'EM'
    when 'EM' then 'EM'
    when 'WE' then 'EW'
    when 'EW' then 'EW'
    when 'MJ' then 'JM'
    when 'JM' then 'JM'
    when 'WJ' then 'JW'
    when 'JW' then 'JW'
    else code
  end,
  name = case upper(btrim(code))
    when 'ME' then 'Elite Men'
    when 'EM' then 'Elite Men'
    when 'WE' then 'Elite Women'
    when 'EW' then 'Elite Women'
    when 'MJ' then 'Junior Men'
    when 'JM' then 'Junior Men'
    when 'WJ' then 'Junior Women'
    when 'JW' then 'Junior Women'
    else name
  end
where upper(btrim(code)) in ('ME', 'EM', 'WE', 'EW', 'MJ', 'JM', 'WJ', 'JW');

commit;

select id, code, name
from public.categories
order by name;
