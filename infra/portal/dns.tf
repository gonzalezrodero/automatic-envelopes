data "aws_route53_zone" "portal" {
  count = var.dns_zone_name == "" ? 0 : 1

  name = var.dns_zone_name
}

resource "aws_route53_record" "portal" {
  for_each = var.dns_cname_records

  zone_id = data.aws_route53_zone.portal[0].zone_id
  name    = each.key
  type    = "CNAME"
  ttl     = 300
  records = [each.value]
}
