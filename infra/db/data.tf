data "terraform_remote_state" "network" {
  backend = "s3"
  config = {
    bucket = "automatic-envelopes-tf-state-${var.aws_account_id}"
    key    = "network/terraform.tfstate"
    region = "eu-west-1"
  }
}