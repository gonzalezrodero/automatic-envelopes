CREATE UNIQUE INDEX mt_doc_tenantprofile_uidx_bot_phone_number_id ON public.mt_doc_tenantprofile USING btree ((data ->> 'BotPhoneNumberId'));
