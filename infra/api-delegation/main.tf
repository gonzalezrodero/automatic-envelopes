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
