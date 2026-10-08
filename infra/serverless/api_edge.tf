provider "aws" {
  region = var.aws_region
}

provider "aws" {
  alias  = "use1"
  region = "us-east-1"
}

# Development records live in the zone this account already owns.
# Production uses its own zone (infra/api-dns) and does not call this module.
data "aws_route53_zone" "api" {
  count = var.api_hostname == "" ? 0 : 1

  name = var.dns_zone_name
}

module "api_edge" {
  count  = var.api_hostname == "" ? 0 : 1
  source = "../modules/api-edge"

  providers = {
    aws.use1 = aws.use1
    aws.dns  = aws
  }

  hostname   = var.api_hostname
  zone_id    = coalesce(one(data.aws_route53_zone.api[*].zone_id), "unused")
  origin_url = aws_lambda_function_url.api_url.function_url
}
