# Domain identity for noreply@core-webhook.eu. Each account (dev and prod) has its own
# identity. DKIM CNAMEs are written here only when this account owns the zone (dev).
# Prod tokens are an output: the zone core-webhook.eu is in the development account.
#
# The phone HMAC key follows infra/db/marten_app.tf: an ephemeral password written once
# into Secrets Manager. It is not stored in Terraform state. Bump the version to rotate.

ephemeral "random_password" "phone_hash_key" {
  length  = 32
  special = false
}

locals {
  phone_hash_key_version = 1
  ses_dkim_tokens        = sort(tolist(try(aws_sesv2_email_identity.sender.dkim_signing_attributes[0].tokens, [])))
  ses_zone_id            = coalesce(one(data.aws_route53_zone.api[*].zone_id), "unused")
}

resource "aws_secretsmanager_secret" "phone_hash_key" {
  name = "${var.project_name}/phone-hash-key"
}

resource "aws_secretsmanager_secret_version" "phone_hash_key" {
  secret_id                = aws_secretsmanager_secret.phone_hash_key.id
  secret_string_wo         = ephemeral.random_password.phone_hash_key.result
  secret_string_wo_version = local.phone_hash_key_version
}

resource "aws_sesv2_email_identity" "sender" {
  email_identity = "core-webhook.eu"
}

resource "aws_route53_record" "ses_dkim" {
  count = var.api_hostname == "" ? 0 : 3

  zone_id = local.ses_zone_id
  name    = "${try(local.ses_dkim_tokens[count.index], "pending${count.index}")}._domainkey.core-webhook.eu"
  type    = "CNAME"
  ttl     = 600
  records = ["${try(local.ses_dkim_tokens[count.index], "pending${count.index}")}.dkim.amazonses.com"]

  lifecycle {
    precondition {
      condition     = length(local.ses_dkim_tokens) == 3
      error_message = "SES did not return 3 DKIM tokens for core-webhook.eu."
    }
  }
}
