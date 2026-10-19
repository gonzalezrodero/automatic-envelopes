# backend.tf
terraform {
  backend "s3" {
    # All values are injected via -backend-config=config/dev/config.remote
  }

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.7"
    }
    postgresql = {
      source  = "cyrilgdn/postgresql"
      version = "~> 1.25"
    }
  }
}