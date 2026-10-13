-- Keep PNG readable for existing venue and round images; new imports use WebP.
begin;

update storage.buckets
set allowed_mime_types = array['image/png', 'image/webp']
where id = 'round-images';

alter table public.round_images
  drop constraint if exists round_images_content_type_png_check;

alter table public.round_images
  drop constraint if exists round_images_content_type_check;

alter table public.round_images
  add constraint round_images_content_type_check
  check (content_type in ('image/png', 'image/webp'));

alter table public.round_images
  drop constraint if exists round_images_png_signature_check;

alter table public.round_images
  drop constraint if exists round_images_signature_check;

alter table public.round_images
  add constraint round_images_signature_check
  check (image_data is null or
    (content_type = 'image/png' and substring(image_data from 1 for 8) = decode('89504E470D0A1A0A', 'hex')) or
    (content_type = 'image/webp' and substring(image_data from 1 for 4) = 'RIFF'::bytea and substring(image_data from 9 for 4) = 'WEBP'::bytea));

alter table public.venue_images
  drop constraint if exists venue_images_content_type_png_check;

alter table public.venue_images
  drop constraint if exists venue_images_content_type_check;

alter table public.venue_images
  add constraint venue_images_content_type_check
  check (content_type in ('image/png', 'image/webp'));

commit;
