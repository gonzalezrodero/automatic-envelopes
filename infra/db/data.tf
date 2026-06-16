data "terraform_remote_state" "network" {
  backend = "s3"
  config = {
    bucket = "automatic-envelopes-tf-state-543704476214"
    key    = "network/terraform.tfstate"
    region = "eu-west-1"
  }
}