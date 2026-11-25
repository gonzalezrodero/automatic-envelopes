resource "aws_cloudwatch_log_group" "postgresql" {
  name              = "/aws/rds/instance/${var.project_name}-db/postgresql"
  retention_in_days = 14
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

# Subscriptions are added by hand outside Terraform so no address lands in this public repo.
# Unencrypted on purpose: alarms cannot publish to a topic using the alias/aws/sns key.
resource "aws_sns_topic" "db_alarms" {
  name = "${var.project_name}-db-alarms"
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
  alarm_actions       = [aws_sns_topic.db_alarms.arn]
}
