terraform {
  required_providers {
    aws = {
      source                = "hashicorp/aws"
      configuration_aliases = [aws.use1, aws.dns]
    }
  }
}

locals {
  origin_domain = trimsuffix(replace(var.origin_url, "https://", ""), "/")
}

# CloudFront certificates have to live in us-east-1.
resource "aws_acm_certificate" "api" {
  provider = aws.use1

  domain_name       = var.hostname
  validation_method = "DNS"

  lifecycle {
    create_before_destroy = true
  }
}

resource "aws_route53_record" "validation" {
  provider = aws.dns

  for_each = {
    for dvo in aws_acm_certificate.api.domain_validation_options : dvo.domain_name => {
      name   = dvo.resource_record_name
      record = dvo.resource_record_value
      type   = dvo.resource_record_type
    }
  }

  allow_overwrite = true
  zone_id         = var.zone_id
  name            = each.value.name
  type            = each.value.type
  ttl             = 300
  records         = [each.value.record]
}

resource "aws_acm_certificate_validation" "api" {
  provider = aws.use1

  certificate_arn         = aws_acm_certificate.api.arn
  validation_record_fqdns = [for record in aws_route53_record.validation : record.fqdn]
}

data "aws_cloudfront_cache_policy" "disabled" {
  provider = aws.use1

  name = "Managed-CachingDisabled"
}

# Lambda function URLs reject a Host header that is not their own hostname.
data "aws_cloudfront_origin_request_policy" "all_except_host" {
  provider = aws.use1

  name = "Managed-AllViewerExceptHostHeader"
}

resource "aws_cloudfront_distribution" "api" {
  provider = aws.use1

  enabled         = true
  is_ipv6_enabled = true
  aliases         = [var.hostname]
  comment         = var.hostname
  price_class     = "PriceClass_100"

  origin {
    domain_name = local.origin_domain
    origin_id   = "api"

    custom_origin_config {
      http_port              = 80
      https_port             = 443
      origin_protocol_policy = "https-only"
      origin_ssl_protocols   = ["TLSv1.2"]
    }
  }

  default_cache_behavior {
    allowed_methods          = ["DELETE", "GET", "HEAD", "OPTIONS", "PATCH", "POST", "PUT"]
    cached_methods           = ["GET", "HEAD"]
    target_origin_id         = "api"
    viewer_protocol_policy   = "redirect-to-https"
    cache_policy_id          = data.aws_cloudfront_cache_policy.disabled.id
    origin_request_policy_id = data.aws_cloudfront_origin_request_policy.all_except_host.id
    compress                 = true
  }

  restrictions {
    geo_restriction {
      restriction_type = "none"
    }
  }

  viewer_certificate {
    acm_certificate_arn      = aws_acm_certificate_validation.api.certificate_arn
    ssl_support_method       = "sni-only"
    minimum_protocol_version = "TLSv1.2_2021"
  }
}

resource "aws_route53_record" "a" {
  provider = aws.dns

  zone_id = var.zone_id
  name    = var.hostname
  type    = "A"

  alias {
    name                   = aws_cloudfront_distribution.api.domain_name
    zone_id                = aws_cloudfront_distribution.api.hosted_zone_id
    evaluate_target_health = false
  }
}

resource "aws_route53_record" "aaaa" {
  provider = aws.dns

  zone_id = var.zone_id
  name    = var.hostname
  type    = "AAAA"

  alias {
    name                   = aws_cloudfront_distribution.api.domain_name
    zone_id                = aws_cloudfront_distribution.api.hosted_zone_id
    evaluate_target_health = false
  }
}

output "api_base" {
  value = "https://${var.hostname}"
}
