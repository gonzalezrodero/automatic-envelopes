resource "aws_cloudwatch_log_group" "postgresql" {
  name              = "/aws/rds/instance/${var.project_name}-db/postgresql"
  retention_in_days = 7
}

resource "aws_cloudwatch_log_metric_filter" "password_auth_failed" {
  name           = "${var.project_name}-pg-password-auth-failed"
  log_group_name = aws_cloudwatch_log_group.postgresql.name
  pattern        = "\"password authentication failed\""

  metric_transformation {
    name      = "PostgresPasswordAuthFailed"
    namespace = "AutomaticEnvelopes/Postgres"
    value     = "1"
  }
}

resource "aws_cloudwatch_metric_alarm" "password_auth_failed" {
  alarm_name          = "${var.project_name}-pg-password-auth-failed"
  comparison_operator = "GreaterThanOrEqualToThreshold"
  evaluation_periods  = 1
  metric_name         = "PostgresPasswordAuthFailed"
  namespace           = "AutomaticEnvelopes/Postgres"
  period              = 300
  statistic           = "Sum"
  threshold           = 1
  treat_missing_data  = "notBreaching"
}
