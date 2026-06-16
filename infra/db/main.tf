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

  ingress {
    description = "PostgreSQL Public Access for Lambdas"
    from_port   = 5432
    to_port     = 5432
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }

  ingress {
    description = "Direct access from Daniel Laptop"
    from_port   = 5432
    to_port     = 5432
    protocol    = "tcp"
    cidr_blocks = ["176.84.209.177/32"]
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
  name       = "${var.project_name}-db-subnet-group"
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
  backup_retention_period = 7

  db_subnet_group_name   = aws_db_subnet_group.db_subnets.name
  vpc_security_group_ids = [aws_security_group.db_sg.id]
  publicly_accessible    = true
  skip_final_snapshot    = true

  manage_master_user_password = true

  # 1. Performance Insights
  performance_insights_enabled          = true
  performance_insights_retention_period = 7

  # 2. Enhanced Monitoring
  monitoring_interval = 60
  monitoring_role_arn = aws_iam_role.rds_monitoring_role.arn
}

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