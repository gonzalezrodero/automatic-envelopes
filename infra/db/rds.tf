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
