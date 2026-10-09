provider "aws" {
  region = var.aws_region
}

provider "aws" {
  alias  = "use1"
  region = "us-east-1"
}

# Own zone in this account. The parent core-webhook.eu zone stays in
# development and only delegates here (infra/api-delegation).
resource "aws_route53_zone" "api" {
  name = var.api_hostname
}

data "terraform_remote_state" "serverless" {
  backend = "s3"

  config = {
    bucket = "automatic-envelopes-tf-state-${var.aws_account_id}"
    key    = "serverless/terraform.tfstate"
    region = var.aws_region
  }
}

module "api_edge" {
  source = "../modules/api-edge"

  providers = {
    aws.use1 = aws.use1
    aws.dns  = aws
  }

  hostname   = var.api_hostname
  zone_id    = aws_route53_zone.api.zone_id
  origin_url = data.terraform_remote_state.serverless.outputs.lambda_url_raw
}

output "api_base" {
  value = "https://${var.api_hostname}"
}

output "name_servers" {
  value = aws_route53_zone.api.name_servers
}
