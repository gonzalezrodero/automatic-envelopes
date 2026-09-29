project_name    = "automatic-envelopes"
app_environment = "Production"
aws_account_id  = "149168707361"
aws_region      = "eu-west-1"

cognito_auth_domain    = "automatic-envelopes-admin-prod"
admin_ui_callback_urls = ["https://admin.core-webhook.eu/auth/callback"]
admin_ui_logout_urls   = ["https://admin.core-webhook.eu/login"]
admin_portal_origins   = ["https://admin.core-webhook.eu"]