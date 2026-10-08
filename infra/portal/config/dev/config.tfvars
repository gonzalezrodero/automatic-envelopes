aws_region     = "eu-west-1"
aws_account_id = "543704476214"

pages_project_name = "automatic-letters-admin-dev"
portal_hostname    = "admin.dev.core-webhook.eu"
dns_zone_name      = "core-webhook.eu"
dns_cname_records  = {
  "admin.dev.core-webhook.eu" = "automatic-letters-admin-dev.pages.dev"
  "admin.core-webhook.eu"     = "automatic-letters-admin.pages.dev"
}
cognito_domain     = "automatic-envelopes-admin-dev.auth.eu-west-1.amazoncognito.com"
github_owner       = "gonzalezrodero"
github_repo        = "automatic-letters-web"
