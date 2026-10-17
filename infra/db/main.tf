provider "aws" {
  region = var.aws_region
}

# ==========================================
# SECURITY GROUP
# ==========================================
resource "aws_security_group" "db_sg" {
  name        = "${var.project_name}-db-sg"
  description = "Allow inbound PostgreSQL traffic"
  vpc_id      = data.terraform_remote_state.network.outputs.vpc_id

  # Lambdas are outside the VPC and use this public endpoint.
  ingress {
    description = "PostgreSQL from Lambdas over the public endpoint"
    from_port   = 5432
    to_port     = 5432
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }
}

# ==========================================
# DB SUBNET GROUP
# ==========================================
resource "aws_db_subnet_group" "db_subnets" {
  name = "${var.project_name}-db-subnet-group"
  # Keep the existing public subnets. Replacing them in-place fails:
  # AWS will not drop subnets that the live RDS instance still uses.
  # Move to private subnets later via a new subnet group, then attach it.
  subnet_ids = data.terraform_remote_state.network.outputs.public_subnet_ids

  tags = {
    Name = "${var.project_name}-db-subnet-group"
  }
}

resource "aws_db_parameter_group" "postgres" {
  name   = "${var.project_name}-postgres16"
  family = "postgres16"

  parameter {
    name         = "rds.force_ssl"
    value        = "1"
    apply_method = "immediate"
  }
}

# ==========================================
# RDS INSTANCE (PostgreSQL)
# ==========================================
resource "aws_db_instance" "postgres" {
  identifier     = "${var.project_name}-db"
  engine         = "postgres"
  engine_version = "16"

  instance_class    = var.db_instance_class
  allocated_storage = var.allocated_storage
  multi_az          = var.multi_az

  max_allocated_storage = 100
  storage_type          = "gp3"

  db_name  = "automaticenvelopes"
  username = "dbadmin"

  storage_encrypted       = true
  backup_retention_period = var.backup_retention_period
  copy_tags_to_snapshot   = var.copy_tags_to_snapshot
  deletion_protection     = var.deletion_protection
  parameter_group_name    = aws_db_parameter_group.postgres.name

  db_subnet_group_name      = aws_db_subnet_group.db_subnets.name
  vpc_security_group_ids    = [aws_security_group.db_sg.id]
  skip_final_snapshot       = var.skip_final_snapshot
  final_snapshot_identifier = var.skip_final_snapshot ? null : "${var.project_name}-db-final"

  manage_master_user_password = true

  # Cost controls: omit retention/role when PI and Enhanced Monitoring are off.
  # AWS rejects monitoring_role_arn when monitoring_interval is 0, and rejects
  # performance_insights_retention_period when performance insights is disabled.
  performance_insights_enabled = false
  monitoring_interval          = 0

  # The instance is already public. Lambda is outside the VPC and there is
  # no NAT, so leave that flag alone until a NAT exists.
  lifecycle {
    ignore_changes = [publicly_accessible]
  }
}

# Kept for a future re-enable of Enhanced Monitoring (not attached while interval=0).
resource "aws_iam_role" "rds_monitoring_role" {
  name = "${var.project_name}-rds-monitoring-role"
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Action = "sts:AssumeRole"
        Effect = "Allow"
        Principal = {
          Service = "monitoring.rds.amazonaws.com"
        }
      }
    ]
  })
}

resource "aws_iam_role_policy_attachment" "rds_monitoring_attach" {
  role       = aws_iam_role.rds_monitoring_role.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonRDSEnhancedMonitoringRole"
}

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