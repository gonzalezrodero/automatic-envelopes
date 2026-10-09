variable "aws_region" {
  type        = string
  description = "AWS region"
}

variable "project_name" {
  type        = string
  description = "Name of the project"
}

variable "app_environment" {
  type        = string
  description = "Environment (dev, stg, prod)"
}

variable "image_tag" {
  type        = string
  description = "Docker image tag to deploy (usually the Git SHA)"
}

variable "aws_account_id" {
  type        = string
  description = "AWS Account ID"
}

variable "admin_ui_callback_urls" {
  type        = list(string)
  description = "List of allowed callback URLs for the Cognito Hosted UI"
}

variable "admin_ui_logout_urls" {
  type        = list(string)
  description = "List of allowed logout URLs for the Cognito Hosted UI"
}

variable "cognito_auth_domain" {
  type        = string
  description = "The globally unique domain prefix for the Cognito Hosted UI URL"
}

variable "admin_portal_origins" {
  type        = list(string)
  description = "Exact browser origins allowed to call the API with credentials. Never use '*'."
}

variable "api_hostname" {
  type        = string
  description = "Public API host on the same site as the portal. Empty skips the custom domain. Production is published by infra/api-dns."
  default     = ""

  validation {
    condition     = var.api_hostname == "" || var.dns_zone_name != ""
    error_message = "dns_zone_name is required when api_hostname is set."
  }
}

variable "dns_zone_name" {
  type        = string
  description = "Route 53 zone in this account that will hold the development API hostname."
  default     = ""
}