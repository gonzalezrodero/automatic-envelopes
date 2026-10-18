# ==========================================
# NIGHTLY STOP / MORNING START (Europe/Madrid)
# ==========================================
resource "aws_iam_role" "rds_scheduler" {
  name = "${var.project_name}-rds-scheduler"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect = "Allow"
        Principal = {
          Service = "scheduler.amazonaws.com"
        }
        Action = "sts:AssumeRole"
      }
    ]
  })
}

resource "aws_iam_role_policy" "rds_scheduler" {
  name = "${var.project_name}-rds-scheduler"
  role = aws_iam_role.rds_scheduler.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect = "Allow"
        Action = [
          "rds:StopDBInstance",
          "rds:StartDBInstance",
          "rds:DescribeDBInstances"
        ]
        Resource = aws_db_instance.postgres.arn
      }
    ]
  })
}

resource "aws_scheduler_schedule_group" "rds" {
  name = "${var.project_name}-rds"
}

resource "aws_scheduler_schedule" "stop_rds" {
  name       = "${var.project_name}-stop-rds"
  group_name = aws_scheduler_schedule_group.rds.name

  schedule_expression          = "cron(0 22 * * ? *)"
  schedule_expression_timezone = "Europe/Madrid"
  description                  = "Stop RDS at 22:00 Europe/Madrid to save idle instance hours"

  flexible_time_window {
    mode = "OFF"
  }

  target {
    arn      = "arn:aws:scheduler:::aws-sdk:rds:stopDBInstance"
    role_arn = aws_iam_role.rds_scheduler.arn
    input = jsonencode({
      DbInstanceIdentifier = aws_db_instance.postgres.identifier
    })
  }
}

resource "aws_scheduler_schedule" "start_rds" {
  name       = "${var.project_name}-start-rds"
  group_name = aws_scheduler_schedule_group.rds.name

  # Disabled while unused: avoid paying daytime instance hours. Re-enable via rds_auto_start=true.
  state = var.rds_auto_start ? "ENABLED" : "DISABLED"

  schedule_expression          = "cron(0 8 * * ? *)"
  schedule_expression_timezone = "Europe/Madrid"
  description                  = "Start RDS at 08:00 Europe/Madrid (disabled when rds_auto_start=false)"

  flexible_time_window {
    mode = "OFF"
  }

  target {
    arn      = "arn:aws:scheduler:::aws-sdk:rds:startDBInstance"
    role_arn = aws_iam_role.rds_scheduler.arn
    input = jsonencode({
      DbInstanceIdentifier = aws_db_instance.postgres.identifier
    })
  }
}
