resource "aws_sqs_queue" "bot_dlq" {
  name = "wolverine-dead-letter-queue"

  message_retention_seconds = 259200 # 3 days

  sqs_managed_sse_enabled = true
}

resource "aws_sqs_queue" "bot_queue" {
  name                       = "${var.project_name}-messages-queue"
  visibility_timeout_seconds = 60

  message_retention_seconds = 345600 # 4 days

  redrive_policy = jsonencode({
    deadLetterTargetArn = aws_sqs_queue.bot_dlq.arn
    maxReceiveCount     = 3
  })
}