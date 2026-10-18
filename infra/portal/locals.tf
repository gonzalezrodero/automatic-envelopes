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
