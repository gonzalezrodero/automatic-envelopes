terraform {
  backend "s3" {
    # All values are injected via -backend-config=config/dev/config.remote
  }

  required_providers {
    cloudflare = {
      source  = "cloudflare/cloudflare"
      version = "~> 5.0"
    }
  }
}
