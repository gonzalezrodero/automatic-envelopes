resource "aws_lambda_function" "worker" {
  function_name = "${var.project_name}-worker"
  role          = aws_iam_role.lambda_exec.arn
  package_type  = "Image"
  image_uri     = "${aws_ecr_repository.backend.repository_url}:${var.image_tag}"

  timeout     = 30
  memory_size = 512

  environment {
    variables = {
      ASPNETCORE_ENVIRONMENT   = var.app_environment
      WhatsApp__BaseUrl        = "https://graph.facebook.com/v19.0/"
      BedrockSettings__Region  = var.aws_region
      BedrockSettings__ModelId = "eu.anthropic.claude-sonnet-4-6"

      SECRET_ARN_MARTEN = data.terraform_remote_state.database.outputs.db_password_secret_arn
      DB_HOST           = data.terraform_remote_state.database.outputs.db_endpoint
      SSM_PATH_WHATSAPP = "/chatbot/dev/whatsapp/"
    }
  }

  image_config {
    command = ["SamaBot.Api::SamaBot.Api.SqsLambdaHandler::FunctionHandler"]
  }
}

resource "aws_lambda_event_source_mapping" "sqs_trigger" {
  event_source_arn = aws_sqs_queue.bot_queue.arn
  function_name    = aws_lambda_function.worker.arn
  batch_size       = 1
}