provider "cloudflare" {
  api_token = var.cloudflare_api_token
}

locals {
  function_url = trimsuffix(data.terraform_remote_state.serverless.outputs.lambda_url_raw, "/")
  api_base     = var.vite_api_base != "" ? var.vite_api_base : local.function_url
  # Plain values copied into the static bundle at build time. They are not server secrets.
  build_env = {
    VITE_API_BASE          = local.api_base
    VITE_COGNITO_DOMAIN    = var.cognito_domain
    VITE_COGNITO_CLIENT_ID = data.terraform_remote_state.serverless.outputs.cognito_client_id
    VITE_BASE              = "/"
    NODE_VERSION           = "22"
  }
  pages_env_vars = {
    for key, value in local.build_env : key => {
      type  = "plain_text"
      value = value
    }
  }
}

# The Cloudflare account must already have the GitHub integration installed.
# Preview deployments stay off so a branch build cannot publish the admin.
resource "cloudflare_pages_project" "admin" {
  account_id        = var.cloudflare_account_id
  name              = var.pages_project_name
  production_branch = "main"

  build_config = {
    build_command   = "npm ci && npm run build"
    destination_dir = "dist"
    root_dir        = "admin"
  }

  source = {
    type = "github"
    config = {
      owner                          = var.github_owner
      repo_name                      = var.github_repo
      production_branch              = "main"
      production_deployments_enabled = true
      pr_comments_enabled            = false
      preview_deployment_setting     = "none"
    }
  }

  deployment_configs = {
    production = {
      env_vars = local.pages_env_vars
    }
    preview = {
      env_vars = local.pages_env_vars
    }
  }
}

resource "cloudflare_pages_domain" "admin" {
  count = var.portal_hostname == "" ? 0 : 1

  account_id   = var.cloudflare_account_id
  project_name = cloudflare_pages_project.admin.name
  name         = var.portal_hostname
}
