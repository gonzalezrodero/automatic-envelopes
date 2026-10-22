provider "aws" {
  region = var.aws_region
}

data "aws_route53_zone" "parent" {
  name = var.zone_name
}

# Points the parent zone at the production account's zone for this name.
resource "aws_route53_record" "delegation" {
  zone_id = data.aws_route53_zone.parent.zone_id
  name    = var.record_name
  type    = "NS"
  ttl     = 300
  records = var.name_servers
}

# Production serverless apply runs first and passes ses_dkim_tokens here.
# Anything other than three tokens publishes nothing, so the first deploy cannot write a placeholder.
locals {
  ses_dkim_tokens = length(var.ses_dkim_tokens) == 3 ? sort(var.ses_dkim_tokens) : []
}

resource "aws_route53_record" "ses_dkim" {
  count = length(local.ses_dkim_tokens)

  zone_id = data.aws_route53_zone.parent.zone_id
  name    = "${local.ses_dkim_tokens[count.index]}._domainkey.${var.zone_name}"
  type    = "CNAME"
  ttl     = 600
  records = ["${local.ses_dkim_tokens[count.index]}.dkim.amazonses.com"]
}
