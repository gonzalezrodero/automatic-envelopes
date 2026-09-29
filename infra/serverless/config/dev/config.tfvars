project_name    = "automatic-envelopes"
app_environment = "Development"
aws_account_id  = "543704476214"
aws_region      = "eu-west-1"

cognito_auth_domain    = "automatic-envelopes-admin-dev"
admin_ui_callback_urls = ["http://localhost:5173/admin/auth/callback"]
admin_ui_logout_urls   = ["http://localhost:5173/admin/login"]
admin_portal_origins   = ["http://localhost:5173"]