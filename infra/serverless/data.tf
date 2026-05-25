# ==============================================================================
# 1. REMOTE STATES (Cross-stack references)
# ==============================================================================
data "terraform_remote_state" "database" {
  backend = "s3"
  config = {
    bucket = "chatbot-tf-state-${var.aws_account_id}"
    key    = "db/terraform.tfstate"
    region = var.aws_region
  }
}

data "terraform_remote_state" "bootstrap" {
  backend = "s3"
  config = {
    bucket = "chatbot-tf-state-${var.aws_account_id}"
    key    = "bootstrap/terraform.tfstate"
    region = var.aws_region
  }
}

# ==============================================================================
# 2. LOCALS
# ==============================================================================
locals {
  db_secret_arn = data.terraform_remote_state.database.outputs.db_password_secret_arn
}