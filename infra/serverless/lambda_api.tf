resource "aws_lambda_function" "api" {
  function_name = "${var.project_name}-api"
  role          = aws_iam_role.lambda_exec.arn
  package_type  = "Image"
  image_uri     = "${aws_ecr_repository.backend.repository_url}:${var.image_tag}"

  memory_size = 1024
  timeout     = 30

  environment {
    variables = {
      ASPNETCORE_ENVIRONMENT   = var.app_environment
      WhatsApp__BaseUrl        = "https://graph.facebook.com/v19.0/"
      BedrockSettings__Region  = var.aws_region
      BedrockSettings__ModelId = "eu.anthropic.claude-sonnet-4-6"

      SECRET_ARN_MARTEN = data.terraform_remote_state.database.outputs.db_password_secret_arn
      DB_HOST           = data.terraform_remote_state.database.outputs.db_endpoint
      SSM_PATH_WHATSAPP = "/automatic-envelopes/dev/whatsapp/"
    }
  }

  image_config {
    command = ["AutomaticEnvelopes.Api"]
  }
}

resource "aws_lambda_function_url" "api_url" {
  function_name      = aws_lambda_function.api.function_name
  authorization_type = "NONE"
}