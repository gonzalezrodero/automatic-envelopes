# Shared WhatsApp credentials. Every tenant uses this one token, app secret,
# and verify token. Lambdas read the path at startup (SSM_PATH_WHATSAPP).
# The value below is a placeholder for parameters that do not exist yet.
# lifecycle ignore_changes keeps a later apply from writing it over a real
# secret. Description and tags are ignored so adopting a hand-created
# parameter does not clear them.
#
# Do not put an import block here. An import block runs on every plan,
# including pull requests, and fails when the parameter is missing. The dev
# plan role also denies ssm:GetParameter*. The API workflow adopts an existing
# parameter before plan (same idea as the ECR repository and log groups).
# Create leaves overwrite at the provider default (false), so a create against
# a parameter that is still outside state fails instead of replacing it.
locals {
  whatsapp_parameter_names = toset([
    "access-token",
    "app-secret",
    "verify-token",
    "phone-number-id",
  ])
}

resource "aws_ssm_parameter" "whatsapp" {
  for_each = local.whatsapp_parameter_names

  name  = "/automatic-envelopes/whatsapp/${each.key}"
  type  = "SecureString"
  value = "replace-me"

  lifecycle {
    ignore_changes = [value, description, tags, tags_all, allowed_pattern]
  }
}
