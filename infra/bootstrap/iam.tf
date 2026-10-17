# ==========================================
# 1. GITHUB OIDC IDENTITY PROVIDER
# ==========================================
resource "aws_iam_openid_connect_provider" "github" {
  url             = "https://token.actions.githubusercontent.com"
  client_id_list  = ["sts.amazonaws.com"]
  thumbprint_list = ["1c58a3a8518e8759bf075b76b750d4f2df264fcd"]
}

# ==========================================
# 2. IAM ROLE FOR GITHUB ACTIONS
# ==========================================
resource "aws_iam_role" "github_actions" {
  name = "${var.project_name}-github-deploy-role"

  # Apply jobs set environment development or production, so `sub` is the
  # environment form. `ref` still has to be main. PRs use the plan role.
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Action = "sts:AssumeRoleWithWebIdentity"
        Effect = "Allow"
        Principal = {
          Federated = aws_iam_openid_connect_provider.github.arn
        }
        Condition = {
          StringEquals = {
            "token.actions.githubusercontent.com:aud" = "sts.amazonaws.com"
            "token.actions.githubusercontent.com:ref" = "refs/heads/main"
            "token.actions.githubusercontent.com:sub" = [
              "repo:${var.github_owner}/${var.github_repo}:environment:development",
              "repo:${var.github_owner}/${var.github_repo}:environment:production",
            ]
          }
        }
      }
    ]
  })
}

resource "aws_iam_role_policy_attachment" "admin_attach" {
  role       = aws_iam_role.github_actions.name
  policy_arn = "arn:aws:iam::aws:policy/AdministratorAccess"
}

resource "aws_iam_role" "github_actions_plan" {
  name = "${var.project_name}-github-plan-role"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Action = "sts:AssumeRoleWithWebIdentity"
        Effect = "Allow"
        Principal = {
          Federated = aws_iam_openid_connect_provider.github.arn
        }
        Condition = {
          StringEquals = {
            "token.actions.githubusercontent.com:aud" = "sts.amazonaws.com"
            "token.actions.githubusercontent.com:sub" = "repo:${var.github_owner}/${var.github_repo}:pull_request"
          }
          StringLike = {
            "token.actions.githubusercontent.com:workflow_ref" = "${var.github_owner}/${var.github_repo}/.github/workflows/pr-terraform.yml@*"
          }
        }
      }
    ]
  })
}

resource "aws_iam_role_policy_attachment" "github_plan_readonly" {
  role       = aws_iam_role.github_actions_plan.name
  policy_arn = "arn:aws:iam::aws:policy/ReadOnlyAccess"
}

resource "aws_iam_role_policy" "github_plan_state" {
  name = "${var.project_name}-github-plan-state"
  role = aws_iam_role.github_actions_plan.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "StateObject"
        Effect   = "Allow"
        Action   = "s3:GetObject"
        Resource = "${aws_s3_bucket.tf_state.arn}/*"
      },
      {
        Sid      = "StateList"
        Effect   = "Allow"
        Action   = "s3:ListBucket"
        Resource = aws_s3_bucket.tf_state.arn
      },
    ]
  })
}

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

# ==========================================
# 4. ECS TASK ROLE (EL QUE USA LA APP)
# ==========================================
resource "aws_iam_role" "ecs_task_role" {
  name = "${var.project_name}-ecs-task-role"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Action = "sts:AssumeRole"
        Effect = "Allow"
        Principal = {
          Service = "ecs-tasks.amazonaws.com"
        }
      }
    ]
  })
}

resource "aws_iam_role_policy_attachment" "attach_bedrock_standard" {
  role       = aws_iam_role.ecs_task_role.name
  policy_arn = aws_iam_policy.bedrock_standard_models.arn
}