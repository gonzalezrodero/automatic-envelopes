# ==========================================
# SECURITY GROUP
# ==========================================
resource "aws_security_group" "db_sg" {
  name        = "${var.project_name}-db-sg"
  description = "Allow inbound PostgreSQL traffic"
  vpc_id      = data.terraform_remote_state.network.outputs.vpc_id

  # Lambdas are outside the VPC and use this public endpoint.
  # Terraform revokes and re-adds the rule if this changes.
  ingress {
    description = "PostgreSQL Public Access for Lambdas"
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
