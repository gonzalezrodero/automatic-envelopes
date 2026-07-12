provider "aws" {
  region = var.aws_region
}

# Fetch available Availability Zones in the region
data "aws_availability_zones" "available" {
  state = "available"
}

# ==========================================
# VPC MODULE
# ==========================================
module "vpc" {
  source  = "terraform-aws-modules/vpc/aws"
  version = "5.8.1"

  name = "${var.project_name}-vpc"
  cidr = var.vpc_cidr

  azs = slice(data.aws_availability_zones.available.names, 0, 2)

  private_subnets = [
    cidrsubnet(var.vpc_cidr, 8, 1), # Dev: 10.0.1.0/24 | Prod: 10.1.1.0/24
    cidrsubnet(var.vpc_cidr, 8, 2)  # Dev: 10.0.2.0/24 | Prod: 10.1.2.0/24
  ]

  public_subnets = [
    cidrsubnet(var.vpc_cidr, 8, 101), # Dev: 10.0.101.0/24 | Prod: 10.1.101.0/24
    cidrsubnet(var.vpc_cidr, 8, 102)  # Dev: 10.0.102.0/24 | Prod: 10.1.102.0/24
  ]

  enable_nat_gateway = false

  enable_dns_hostnames = true
  enable_dns_support   = true
}