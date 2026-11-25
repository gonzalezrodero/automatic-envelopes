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

data "aws_caller_identity" "current" {}

resource "aws_kms_key" "db_alarms" {
  description         = "Encrypts the RDS password-auth-failed alarm topic"
  enable_key_rotation = true

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid    = "EnableRootKeyAdmin"
        Effect = "Allow"
        Principal = {
          AWS = "arn:aws:iam::${data.aws_caller_identity.current.account_id}:root"
        }
        Action   = "kms:*"
        Resource = "*"
      },
      {
        Sid    = "AllowCloudWatchAlarms"
        Effect = "Allow"
        Principal = {
          Service = "cloudwatch.amazonaws.com"
        }
        Action = [
          "kms:Decrypt",
          "kms:GenerateDataKey*"
        ]
        Resource = "*"
        Condition = {
          StringEquals = {
            "aws:SourceAccount" = data.aws_caller_identity.current.account_id
          }
          ArnLike = {
            "aws:SourceArn" = "arn:aws:cloudwatch:${var.aws_region}:${data.aws_caller_identity.current.account_id}:alarm:*"
          }
        }
      }
    ]
  })
}

# Customer-managed key because alarms cannot use alias/aws/sns.
# Subscriptions stay manual.
resource "aws_sns_topic" "db_alarms" {
  name              = "${var.project_name}-db-alarms"
  kms_master_key_id = aws_kms_key.db_alarms.arn
}

resource "aws_sns_topic_policy" "db_alarms" {
  arn = aws_sns_topic.db_alarms.arn

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid    = "AllowAccountOwner"
        Effect = "Allow"
        Principal = {
          AWS = "*"
        }
        Action = [
          "SNS:GetTopicAttributes",
          "SNS:SetTopicAttributes",
          "SNS:AddPermission",
          "SNS:RemovePermission",
          "SNS:DeleteTopic",
          "SNS:Subscribe",
          "SNS:ListSubscriptionsByTopic",
          "SNS:Publish"
        ]
        Resource = aws_sns_topic.db_alarms.arn
        Condition = {
          StringEquals = {
            "AWS:SourceOwner" = data.aws_caller_identity.current.account_id
          }
        }
      },
      {
        Sid    = "AllowCloudWatchPublish"
        Effect = "Allow"
        Principal = {
          Service = "cloudwatch.amazonaws.com"
        }
        Action   = "sns:Publish"
        Resource = aws_sns_topic.db_alarms.arn
        Condition = {
          StringEquals = {
            "aws:SourceAccount" = data.aws_caller_identity.current.account_id
          }
          ArnLike = {
            "aws:SourceArn" = aws_cloudwatch_metric_alarm.password_auth_failed.arn
          }
        }
      }
    ]
  })
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
