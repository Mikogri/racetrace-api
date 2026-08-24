begin;

alter table public.sessions
  add column if not exists chronorace_url text;

do $$
begin
  if not exists (
    select 1
    from pg_constraint
    where conrelid = 'public.sessions'::regclass
      and conname = 'sessions_chronorace_url_check'
  ) then
    alter table public.sessions
      add constraint sessions_chronorace_url_check
      check (
        chronorace_url is null
        or (
          length(chronorace_url) <= 2048
          and chronorace_url ~ '^https://prod[.]chronorace[.]be/'
        )
      );
  end if;
end
$$;

comment on column public.sessions.chronorace_url is
  'Public ChronoRace JSON source used by the frontend for direct live polling.';

commit;
