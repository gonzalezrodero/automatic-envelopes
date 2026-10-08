terraform {
  backend "s3" {
    # All values are injected via -backend-config=config/prod/config.remote
  }

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }
}
