# ==========================================
# 3. BEDROCK "STANDARD MODELS" POLICY
# ==========================================
data "aws_caller_identity" "current" {}

resource "aws_iam_policy" "bedrock_standard_models" {
  name        = "${var.project_name}-bedrock-standard-access"
  description = "Allows Claude Sonnet 4.6 and Titan models with marketplace validation"

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect = "Allow"
        Action = "bedrock:InvokeModel"
        Resource = [
          "arn:aws:bedrock:*::foundation-model/anthropic.claude-sonnet-4-6",
          "arn:aws:bedrock:*::inference-profile/eu.anthropic.claude-sonnet-4-6",
          "arn:aws:bedrock:${var.aws_region}::foundation-model/amazon.titan-text-lite-v1",
          "arn:aws:bedrock:${var.aws_region}::foundation-model/amazon.titan-text-express-v1",
          "arn:aws:bedrock:${var.aws_region}::foundation-model/amazon.titan-embed-text-v2:0"
        ]
      },
      {
        Effect   = "Allow"
        Action   = "bedrock:ListFoundationModels"
        Resource = "*"
      },
      {
        Effect = "Allow"
        Action = [
          "aws-marketplace:ViewSubscriptions",
          "aws-marketplace:Subscribe"
        ]
        Resource = "*"
      }
    ]
  })
}
