data "terraform_remote_state" "serverless" {
  backend = "s3"
  config = {
    bucket = "automatic-envelopes-tf-state-${var.aws_account_id}"
    key    = "serverless/terraform.tfstate"
    region = var.aws_region
  }
}
