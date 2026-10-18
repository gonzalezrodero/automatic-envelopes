# Own zone in this account. The parent core-webhook.eu zone stays in
# development and only delegates here (infra/api-delegation).
resource "aws_route53_zone" "api" {
  name = var.api_hostname
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
