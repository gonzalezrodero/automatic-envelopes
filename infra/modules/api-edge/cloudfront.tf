locals {
  origin_domain = trimsuffix(replace(var.origin_url, "https://", ""), "/")
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

  # Session cookies stay out of the log files.
  logging_config {
    include_cookies = false
    bucket          = aws_s3_bucket.logs.bucket_domain_name
    prefix          = "cloudfront/"
  }

  depends_on = [aws_s3_bucket_acl.logs]

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
