# Domain identity for noreply@core-webhook.eu. Each account (dev and prod) has its own
# identity. DKIM CNAMEs are written here only when this account owns the zone (dev).
# Prod tokens are an output: the zone core-webhook.eu is in the development account.

resource "random_password" "phone_hash_key" {
  length  = 32
  special = false
}

resource "aws_ssm_parameter" "phone_hash_key" {
  name  = "/automatic-envelopes/usage/phone-hash-key"
  type  = "SecureString"
  value = random_password.phone_hash_key.result
}

resource "aws_sesv2_email_identity" "sender" {
  email_identity = "core-webhook.eu"
}

locals {
  ses_dkim_tokens = sort(tolist(try(aws_sesv2_email_identity.sender.dkim_signing_attributes[0].tokens, [])))
  ses_zone_id     = coalesce(one(data.aws_route53_zone.api[*].zone_id), "unused")
}

resource "aws_route53_record" "ses_dkim" {
  count = var.api_hostname == "" ? 0 : 3

  zone_id = local.ses_zone_id
  name    = "${local.ses_dkim_tokens[count.index]}._domainkey.core-webhook.eu"
  type    = "CNAME"
  ttl     = 600
  records = ["${local.ses_dkim_tokens[count.index]}.dkim.amazonses.com"]
}
