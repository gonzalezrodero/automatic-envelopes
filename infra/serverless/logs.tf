resource "aws_cloudwatch_log_group" "api" {
  name              = "/aws/lambda/${var.project_name}-api"
  retention_in_days = 14
}

resource "aws_cloudwatch_log_group" "worker" {
  name              = "/aws/lambda/${var.project_name}-worker"
  retention_in_days = 14
}

# Lambda created these groups on first run. Import adopts them.
import {
  to = aws_cloudwatch_log_group.api
  id = "/aws/lambda/${var.project_name}-api"
}

import {
  to = aws_cloudwatch_log_group.worker
  id = "/aws/lambda/${var.project_name}-worker"
}
