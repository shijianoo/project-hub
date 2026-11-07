import dayjs from "dayjs";
import {
  EmailDeliveryStatus,
  NotificationSeverity,
  NotificationType
} from "@/api/projecthub/types";

export interface NotificationVisual {
  label: string;
  icon: string;
  color: string;
  background: string;
}

const typeVisuals: Partial<Record<NotificationType, NotificationVisual>> = {
  [NotificationType.DailyTimesheetReminder]: {
    label: "日工时提醒",
    icon: "ri:timer-flash-line",
    color: "#d97706",
    background: "#fff7e6"
  },
  [NotificationType.WeeklyTimesheetReminder]: {
    label: "周确认提醒",
    icon: "ri:calendar-check-line",
    color: "#c2410c",
    background: "#fff1eb"
  },
  [NotificationType.ProjectMemberAdded]: {
    label: "项目成员",
    icon: "ri:user-add-line",
    color: "#059669",
    background: "#eafaf3"
  },
  [NotificationType.ProjectMemberRemoved]: {
    label: "项目成员",
    icon: "ri:user-unfollow-line",
    color: "#dc2626",
    background: "#fff0f0"
  },
  [NotificationType.ProjectRoleChanged]: {
    label: "项目角色",
    icon: "ri:shield-user-line",
    color: "#3157d5",
    background: "#edf1ff"
  },
  [NotificationType.ProjectAssetCreated]: {
    label: "项目资产",
    icon: "ri:folder-upload-line",
    color: "#2563eb",
    background: "#eaf2ff"
  },
  [NotificationType.ProjectAssetVersionUploaded]: {
    label: "资产版本",
    icon: "ri:git-commit-line",
    color: "#7c3aed",
    background: "#f3edff"
  }
};

/** 返回通知类型对应的统一图标与色彩。 */
export function getNotificationVisual(
  type: NotificationType,
  severity = NotificationSeverity.Info
): NotificationVisual {
  if (typeVisuals[type]) return typeVisuals[type]!;
  if (severity === NotificationSeverity.Error) {
    return {
      label: "重要通知",
      icon: "ri:error-warning-line",
      color: "#dc2626",
      background: "#fff0f0"
    };
  }
  return {
    label: "系统通知",
    icon: "ri:notification-3-line",
    color: "#3157d5",
    background: "#edf1ff"
  };
}

/** 将通知时间显示为便于快速浏览的相对时间。 */
export function formatNotificationTime(value: string): string {
  const time = dayjs(value);
  const now = dayjs();
  const minutes = now.diff(time, "minute");
  if (minutes < 1) return "刚刚";
  if (minutes < 60) return `${minutes} 分钟前`;
  const hours = now.diff(time, "hour");
  if (hours < 24) return `${hours} 小时前`;
  if (now.diff(time, "day") < 7) return time.format("dddd HH:mm");
  return time.format("YYYY-MM-DD HH:mm");
}

export function getEmailDeliveryLabel(status: EmailDeliveryStatus): string {
  const labels: Record<EmailDeliveryStatus, string> = {
    [EmailDeliveryStatus.NotRequested]: "仅站内",
    [EmailDeliveryStatus.Pending]: "邮件待发送",
    [EmailDeliveryStatus.Processing]: "邮件发送中",
    [EmailDeliveryStatus.Sent]: "邮件已发送",
    [EmailDeliveryStatus.Failed]: "邮件发送失败",
    [EmailDeliveryStatus.SkippedNoEmail]: "未配置邮箱",
    [EmailDeliveryStatus.SkippedSmtpUnavailable]: "SMTP 未启用"
  };
  return labels[status];
}
