variable "aws_region" {
  type        = string
  description = "AWS region of the production API"
}

variable "aws_account_id" {
  type        = string
  description = "Production account that holds the API and this stack's state"
}

variable "api_hostname" {
  type        = string
  description = "Production API hostname. This stack creates a Route 53 zone with this name."
}
