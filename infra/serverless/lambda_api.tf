resource "aws_lambda_function" "api" {
  function_name = "${var.project_name}-api"
  role          = aws_iam_role.lambda_exec.arn
  package_type  = "Image"
  image_uri     = "${aws_ecr_repository.backend.repository_url}:${var.image_tag}"

  memory_size = 512
  timeout     = 30

  environment {
    variables = {
      ASPNETCORE_ENVIRONMENT = var.app_environment

      WhatsApp__BaseUrl        = "https://graph.facebook.com/v19.0/"
      BedrockSettings__Region  = var.aws_region
      BedrockSettings__ModelId = "eu.anthropic.claude-sonnet-4-6"

      SECRET_ARN_MARTEN = data.terraform_remote_state.database.outputs.db_password_secret_arn
      DB_HOST           = data.terraform_remote_state.database.outputs.db_endpoint
      SSM_PATH_WHATSAPP = "/automatic-envelopes/whatsapp/"

      COGNITO_USER_POOL_ID          = aws_cognito_user_pool.admin_pool.id
      COGNITO_CLIENT_ID             = aws_cognito_user_pool_client.spa_client.id
      COGNITO_DOMAIN                = "${aws_cognito_user_pool_domain.admin_domain.domain}.auth.${var.aws_region}.amazoncognito.com"
      COGNITO_ALLOWED_REDIRECT_URIS = join(",", var.admin_ui_callback_urls)
      COGNITO_LOGOUT_URIS           = join(",", var.admin_ui_logout_urls)
      ADMIN_PORTAL_ORIGINS          = join(",", var.admin_portal_origins)
    }
  }

  image_config {
    command = ["AutomaticEnvelopes.Api"]
  }
}

# Credentialed CORS is applied by the ASP.NET pipeline (exact origins).
# A function URL cors block would answer preflight itself and cannot send credentials with '*'.
resource "aws_lambda_function_url" "api_url" {
  function_name      = aws_lambda_function.api.function_name
  authorization_type = "NONE"
}