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

# ==========================================
# PARAMETER GROUP
# ==========================================
# Pins rds.force_ssl on the existing instance. The parameter is dynamic, but AWS
# does not use a newly associated parameter group until the instance reboots.
# parameter_group_name is an in-place change: it does not replace the instance.
# apply_immediately stays at the provider default (false), so that reboot waits
# for the maintenance window. PostgreSQL 16 defaults this parameter to 1; this
# group keeps it on if the live instance is still using an older value.
resource "aws_db_parameter_group" "postgres" {
  name        = "${var.project_name}-postgres16"
  family      = "postgres16"
  description = "Require SSL connections to PostgreSQL 16."

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
  deletion_protection     = var.deletion_protection

  db_subnet_group_name      = aws_db_subnet_group.db_subnets.name
  parameter_group_name      = aws_db_parameter_group.postgres.name
  vpc_security_group_ids    = [aws_security_group.db_sg.id]
  skip_final_snapshot       = var.skip_final_snapshot
  final_snapshot_identifier = var.skip_final_snapshot ? null : "${var.project_name}-db-final"

  manage_master_user_password = true

  enabled_cloudwatch_logs_exports = ["postgresql"]

  depends_on = [aws_cloudwatch_log_group.postgresql]

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
